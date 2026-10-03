using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Supervisor.Contracts;

namespace SharedRuntime;

public static class RuntimeMetrics
{
    public static string OwnCgroup()
    {
        var relative = File.ReadLines("/proc/self/cgroup").Single(line => line.StartsWith("0::", StringComparison.Ordinal))[3..];
        return Path.Combine("/sys/fs/cgroup", relative.TrimStart('/'));
    }

    public static async Task<ContainerStats> SampleCgroup(string path, CancellationToken cancellation)
    {
        long Cpu() => File.ReadLines(Path.Combine(path, "cpu.stat")).Where(line => line.StartsWith("usage_usec ", StringComparison.Ordinal))
            .Select(line => long.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture)).Single();
        var first = Cpu();
        var clock = Stopwatch.StartNew();
        await Task.Delay(200, cancellation);
        var cpu = (Cpu() - first) / 1_000_000.0 / clock.Elapsed.TotalSeconds * 100;
        var memory = long.Parse(File.ReadAllText(Path.Combine(path, "memory.current")).Trim(), CultureInfo.InvariantCulture);
        var limit = long.Parse(File.ReadAllText(Path.Combine(path, "memory.max")).Trim(), CultureInfo.InvariantCulture);
        var io = File.ReadLines(Path.Combine(path, "io.stat")).SelectMany(line => line.Split(' ').Skip(1))
            .Select(field => field.Split('=')).GroupBy(fields => fields[0])
            .ToDictionary(group => group.Key, group => group.Sum(fields => long.Parse(fields[1], CultureInfo.InvariantCulture)));
        var interfaces = File.ReadLines("/proc/net/dev").Skip(2).Where(line => !line.TrimStart().StartsWith("lo:", StringComparison.Ordinal))
            .Select(line => line.Split(':')[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return new(cpu, memory, limit, memory * 100.0 / limit,
            interfaces.Sum(fields => long.Parse(fields[0], CultureInfo.InvariantCulture)),
            interfaces.Sum(fields => long.Parse(fields[8], CultureInfo.InvariantCulture)),
            io.GetValueOrDefault("rbytes"), io.GetValueOrDefault("wbytes"));
    }

    public static CoreMetadata Network()
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().SelectMany(item =>
            item.GetIPProperties().UnicastAddresses.Select(address => (item, address)))
            .First(pair => pair.address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(pair.address.Address));
        var mask = network.address.IPv4Mask.GetAddressBytes();
        var subnet = network.address.Address.GetAddressBytes().Zip(mask, (address, bits) => (byte)(address & bits)).ToArray();
        var route = File.ReadLines("/proc/net/route").Skip(1).Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .First(fields => fields[0] == network.item.Name && fields[1] == "00000000");
        var gateway = new IPAddress(BitConverter.GetBytes(uint.Parse(route[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToString();
        var dns = File.ReadLines("/etc/resolv.conf").Where(line => line.StartsWith("nameserver ", StringComparison.Ordinal))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1])
            .First(value => IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork);
        return new(network.address.Address.ToString(), new DockerNetwork(network.item.Name,
            new IPAddress(subnet) + "/" + network.address.PrefixLength, gateway, dns));
    }
}
