using System.Runtime.InteropServices;
using SharedRuntime;
using Supervisor.Contracts;

namespace SupervisorOperator;

public sealed class P0ReadModels(IConfiguration configuration, InstanceOptionsStore options, GatewayClient gateway)
{
    public const string BuildVersion = "0.0.0";
    public const string Limitation = "Kubernetes operator preview: add-on installation, backups, host control and updates are unavailable";
    private string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "amd64";
    private string CoreVersion => configuration["Instance:CoreVersion"] ?? "2026.9.4";
    private string NodeName => configuration["Instance:NodeName"] ?? Environment.MachineName;
    private string? OperatingSystem => configuration["Instance:OperatingSystem"];

    public RootInfo Root() => new(BuildVersion, CoreVersion, null, "unavailable (Kubernetes CRI)", NodeName, OperatingSystem, [],
        null, null, Arch, "running", [Arch], false, "stable", "info", options.Read().Timezone);

    public async Task<CoreInfo> Core(CancellationToken cancellation)
    {
        var metadata = await gateway.Read<CoreMetadata>("/core/metadata", cancellation);
        var current = options.Read();
        return new(CoreVersion, CoreVersion, false, null, metadata.IpAddress, Arch,
            configuration["Instance:CoreImage"] ?? "ghcr.io/home-assistant/home-assistant:2026.9.4", true,
            current.Port, current.Ssl, false, null, null, false, false);
    }

    public SupervisorInfo Supervisor()
    {
        var current = options.Read();
        var address = configuration["Instance:PodIp"] ?? "127.0.0.1";
        return new(BuildVersion, BuildVersion, false, "stable", Arch, false, true, address, current.Timezone, "info",
            false, false, current.Diagnostics, false, current.Country, false,
            new Dictionary<string, bool> { ["supervisor_v2_api"] = false }, 0, [], []);
    }

    public HostInfo Host()
    {
        // These disk values describe the operator's persisted compatibility
        // state filesystem, not unobserved node disks or production CSI quotas.
        var disk = new DriveInfo(configuration["Supervisor:StateDirectory"] ?? Path.GetTempPath());
        const double gib = 1024.0 * 1024 * 1024;
        return new(null, null, null, null, null, "kubernetes", disk.AvailableFreeSpace / gib, disk.TotalSize / gib,
            (disk.TotalSize - disk.TotalFreeSpace) / gib, null, [], NodeName, null,
            Environment.OSVersion.VersionString, OperatingSystem, options.Read().Timezone, DateTimeOffset.UtcNow,
            null, null, null, null, null, null);
    }

    public async Task<NetworkInfo> Network(CancellationToken cancellation) =>
        new([], (await gateway.Read<CoreMetadata>("/core/metadata", cancellation)).Network, null, false);

    public async Task<ContainerStats> SupervisorStats(CancellationToken cancellation) =>
        await RuntimeMetrics.SampleCgroup(RuntimeMetrics.OwnCgroup(), cancellation);
}
