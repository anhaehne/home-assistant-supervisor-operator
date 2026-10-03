using System.Text.Json;

namespace SupervisorOperator;

public sealed record InstanceOptions(string Timezone = "UTC", string? Country = null, int Port = 80, bool Ssl = false, bool Diagnostics = false);

// Compatibility-spike state. P1 moves this store behind CRD-owned instance
// intent; transport code does not depend on the persistence representation.
public sealed class InstanceOptionsStore
{
    private readonly string? path;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private InstanceOptions value;

    public InstanceOptionsStore(IConfiguration configuration)
    {
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

    public InstanceOptions Read() => Volatile.Read(ref value);

    public async Task Update(Func<InstanceOptions, InstanceOptions> update, CancellationToken cancellation)
    {
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
