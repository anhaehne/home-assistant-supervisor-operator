using System.Text.Json;
using System.Net;
using k8s.Autorest;
using KubeOps.KubernetesClient;
using SupervisorOperator.Lifecycle;

namespace SupervisorOperator;

public sealed record InstanceOptions(string Timezone = "UTC", string? Country = null, int Port = 80, bool Ssl = false, bool Diagnostics = false);

// Compatibility-spike state. P1 moves this store behind CRD-owned instance
// intent; transport code does not depend on the persistence representation.
public sealed class InstanceOptionsStore
{
    private readonly string? path;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private InstanceOptions value;
    private readonly IKubernetesClient? client;
    private readonly string? installationNamespace;

    public InstanceOptionsStore(IConfiguration configuration, IServiceProvider? services = null)
    {
        if (configuration.GetValue<bool>("Operator:Enabled"))
        {
            client = services?.GetRequiredService<IKubernetesClient>() ?? throw new InvalidOperationException("The installed option store requires the guarded Kubernetes client");
            installationNamespace = configuration["Kubernetes:Namespace"];
            value = new();
            return;
        }
        var directory = configuration["Supervisor:StateDirectory"];
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "instance-options.json");
        }
        value = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<InstanceOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid persisted instance options.")
            : new InstanceOptions();
    }

    public InstanceOptions Read() => client is null ? Volatile.Read(ref value) :
        (client.GetAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, installationNamespace).GetAwaiter().GetResult()
            ?? throw new ApiValidationException("The instance is unavailable")).Spec.Options;

    public async Task Update(Func<InstanceOptions, InstanceOptions> update, CancellationToken cancellation)
    {
        if (client is not null)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var instance = await client.GetAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, installationNamespace, cancellation)
                    ?? throw new ApiValidationException("The instance is unavailable");
                var next = update(instance.Spec.Options);
                if (next == instance.Spec.Options) return;
                if (instance.Spec.Ownership == "GitOps") throw new ApiValidationException("Instance options are managed by GitOps");
                instance.Spec.Options = next;
                try { await client.UpdateAsync(instance, cancellation); return; }
                catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict) { }
            }
            throw new ApiValidationException("Concurrent instance changes; retry the options request");
        }
        await mutex.WaitAsync(cancellation);
        try
        {
            var next = update(value);
            if (path is not null)
            {
                var temporary = path + ".tmp";
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, next, cancellationToken: cancellation);
                    await stream.FlushAsync(cancellation);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            Volatile.Write(ref value, next);
        }
        finally { mutex.Release(); }
    }
}
