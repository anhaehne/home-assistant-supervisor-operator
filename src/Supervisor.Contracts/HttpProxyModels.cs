using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Supervisor.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpProxySettings(string[] TrustedProxies);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpProxyConfirmation(string[] TrustedProxies, string ExpectedFingerprint);
public sealed record HttpProxyState(bool StableMatches, bool PendingExists, bool PendingMatches,
    bool PendingActive, bool PendingError, string DesiredFingerprint, string? PendingFingerprint);

public static class HttpProxyValidation
{
    public static string[] Normalize(string[]? proxies)
    {
        if (proxies is null || proxies.Length > 128) throw new ArgumentException("Provide at most 128 trusted proxy addresses or networks.");
        return proxies.Select(NormalizeAddress).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('%')) throw new ArgumentException("Invalid trusted proxy address or network.");
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)) throw new ArgumentException("Invalid trusted proxy address or network.");
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        if (ipv4 && (parts[0].Split('.').Length != 4 || parts[0].Split('.').Any(part => part.Length == 0 || part.Length > 3 || part.Any(c => c < '0' || c > '9') || (part.Length > 1 && part[0] == '0'))))
            throw new ArgumentException("IPv4 addresses must use four decimal octets.");
        if (!ipv4 && (!parts[0].Contains(':') || address.IsIPv4MappedToIPv6)) throw new ArgumentException("Invalid trusted proxy address.");
        var bits = ipv4 ? 32 : 128;
        var prefix = bits;
        if (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(c => c < '0' || c > '9') || !int.TryParse(parts[1], out prefix) || prefix < 1 || prefix > bits))
            throw new ArgumentException("Invalid trusted proxy network prefix; trusting all addresses is prohibited.");
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bits; bit++)
            if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != 0) throw new ArgumentException("Trusted proxy CIDRs must specify the network address.");
        return $"{address}/{prefix}";
    }
}
