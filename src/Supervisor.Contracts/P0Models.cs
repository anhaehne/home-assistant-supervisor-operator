namespace Supervisor.Contracts;

// Names are serialized using snake_case. Nullable metadata is deliberately kept
// on the wire; a Kubernetes installation must never claim HAOS identity.
public sealed record RootInfo(string Supervisor, string? Homeassistant, string? Hassos, string Docker,
    string? Hostname, string? OperatingSystem, string[] Features, string? Machine, string? MachineId,
    string Arch, string State, string[] SupportedArch, bool Supported, string Channel, string Logging, string Timezone);
public sealed record CoreInfo(string? Version, string? VersionLatest, bool UpdateAvailable, string? Machine,
    string IpAddress, string? Arch, string Image, bool Boot, int Port, bool Ssl, bool Watchdog,
    string? AudioInput, string? AudioOutput, bool BackupsExcludeDatabase, bool DuplicateLogFile);
public sealed record SupervisorInfo(string Version, string? VersionLatest, bool UpdateAvailable, string Channel,
    string? Arch, bool Supported, bool Healthy, string IpAddress, string? Timezone, string Logging,
    bool Debug, bool DebugBlock, bool Diagnostics, bool AutoUpdate, string? Country, bool DetectBlockingIo,
    Dictionary<string, bool> FeatureFlags, int WaitBoot, object[] Addons, object[] AddonsRepositories);
public sealed record OsInfo(string? Version = null, string? VersionLatest = null, string? VersionPending = null,
    bool UpdateAvailable = false, string? Board = null, string? Boot = null, string? DataDisk = null,
    Dictionary<string, object>? BootSlots = null);
public sealed record HostInfo(string? AgentVersion, string? ApparmorVersion, string? Chassis, string? Virtualization,
    string? Cpe, string? Deployment, double DiskFree, double DiskTotal, double DiskUsed, double? DiskLifeTime,
    string[] Features, string? Hostname, string? LlmnrHostname, string? Kernel, string? OperatingSystem,
    string? Timezone, DateTimeOffset? DtUtc, bool? DtSynchronized, bool? UseNtp, double? StartupTime,
    long? BootTimestamp, bool? BroadcastLlmnr, bool? BroadcastMdns);
public sealed record StoreInfo(object[] Addons, object[] Repositories);
public sealed record AddonsInfo(object[] Addons);
public sealed record JobsInfo(string[] IgnoreConditions, object[] Jobs);
public sealed record MountsInfo(string? DefaultBackupMount, object[] Mounts);
public sealed record IngressInfo(Dictionary<string, object> Panels);
public sealed record ResolutionInfo(string[] Unsupported, string[] Unhealthy, object[] Suggestions, object[] Issues, object[] Checks);
public sealed record DockerNetwork(string Interface, string Address, string Gateway, string Dns);
public sealed record NetworkInfo(object[] Interfaces, DockerNetwork Docker, bool? HostInternet, bool SupervisorInternet);
public sealed record ContainerStats(double CpuPercent, long MemoryUsage, long MemoryLimit, double MemoryPercent,
    long NetworkRx, long NetworkTx, long BlkRead, long BlkWrite);
public sealed record CoreMetadata(string IpAddress, DockerNetwork Network);
