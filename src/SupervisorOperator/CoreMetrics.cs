using System.Diagnostics;
using System.Globalization;
using k8s;
using Supervisor.Contracts;

namespace SupervisorOperator;

// The operator reads a fixed set of counters in its singleton Core container.
// No node credentials, generic commands, Pod names, or paths come from callers.
public sealed class CoreMetrics : IDisposable
{
    private readonly Kubernetes? client;
    private readonly string installationNamespace;
    private const string ReadCounters = "cat /sys/fs/cgroup/cpu.stat; echo MEMORY; cat /sys/fs/cgroup/memory.current /sys/fs/cgroup/memory.max; echo IO; cat /sys/fs/cgroup/io.stat; echo NETWORK; cat /proc/net/dev";

    public CoreMetrics(IConfiguration configuration)
    {
        installationNamespace = configuration["Kubernetes:Namespace"] ?? "";
        // Ambient service discovery in an outer development Pod must never
        // select that hosting cluster. The installed workload explicitly opts
        // in and supplies its own namespace through the downward API.
        if (configuration.GetValue<bool>("Kubernetes:UseInClusterCredentials"))
        {
            if (string.IsNullOrWhiteSpace(installationNamespace) || !KubernetesClientConfiguration.IsInCluster())
                throw new InvalidOperationException("The installed metrics adapter requires an explicit namespace and in-cluster credentials.");
            client = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
        }
    }

    public async Task<ContainerStats> Read(CancellationToken cancellation)
    {
        if (client is null) throw new ApiValidationException("Core metrics require the installed namespace-scoped adapter");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var first = await Snapshot(timeout.Token);
        var watch = Stopwatch.StartNew();
        await Task.Delay(200, timeout.Token);
        var second = await Snapshot(timeout.Token);
        return new((second.Cpu - first.Cpu) / 1_000_000.0 / watch.Elapsed.TotalSeconds * 100,
            second.Memory, second.Limit, second.Memory * 100.0 / second.Limit,
            second.Rx, second.Tx, second.Read, second.Write);
    }

    private async Task<Counters> Snapshot(CancellationToken cancellation)
    {
        string output = "";
        var code = await client!.NamespacedPodExecAsync("core-0", installationNamespace, "core", ["sh", "-ec", ReadCounters], false,
            async (_, stdout, stderr) =>
            {
                var read = new StreamReader(stdout).ReadToEndAsync(cancellation);
                var error = new StreamReader(stderr).ReadToEndAsync(cancellation);
                await Task.WhenAll(read, error);
                output = await read;
            }, cancellation);
        if (code != 0) throw new ApiValidationException("Core counters are unavailable");
        return Parse(output);
    }

    public static Counters Parse(string output)
    {
        var sections = output.Split(["MEMORY\n", "IO\n", "NETWORK\n"], StringSplitOptions.None);
        if (sections.Length != 4) throw new InvalidDataException("Invalid Core counter response");
        long Number(string value) => long.Parse(value.Trim(), CultureInfo.InvariantCulture);
        var cpu = sections[0].Split('\n').Single(line => line.StartsWith("usage_usec ", StringComparison.Ordinal));
        var memory = sections[1].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var io = sections[2].Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(line => line.Split(' ').Skip(1))
            .Select(field => field.Split('=')).GroupBy(fields => fields[0]).ToDictionary(group => group.Key, group => group.Sum(fields => Number(fields[1])));
        var interfaces = sections[3].Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2)
            .Where(line => !line.TrimStart().StartsWith("lo:", StringComparison.Ordinal))
            .Select(line => line.Split(':')[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return new(Number(cpu.Split(' ')[1]), Number(memory[0]), Number(memory[1]), io.GetValueOrDefault("rbytes"), io.GetValueOrDefault("wbytes"),
            interfaces.Sum(fields => Number(fields[0])), interfaces.Sum(fields => Number(fields[8])));
    }

    public void Dispose() => client?.Dispose();
    public sealed record Counters(long Cpu, long Memory, long Limit, long Read, long Write, long Rx, long Tx);
}
