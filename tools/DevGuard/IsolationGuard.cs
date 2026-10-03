using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevGuard;

public sealed record ClusterIdentity(string Name, string DaemonId, string? NodeId = null, string? KubeconfigHash = null);

public static partial class IsolationGuard
{
    public const string RuntimeEndpoint = "unix:///run/paseo-docker/docker.sock";

    public static void ValidateEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var key in new[] { "KUBECONFIG", "USE_EXISTING_CLUSTER", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "CONTAINER_HOST" })
            if (environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                throw new InvalidOperationException($"Unset {key}; project tests cannot inherit cluster or runtime configuration.");
        if (environment.TryGetValue("DOCKER_HOST", out var host) && !string.IsNullOrEmpty(host) && host != RuntimeEndpoint)
            throw new InvalidOperationException("DOCKER_HOST must refer to the provisioned dedicated runner socket.");
        if (environment.TryGetValue("KIND_EXPERIMENTAL_PROVIDER", out var provider) && !string.IsNullOrEmpty(provider) && provider != "docker")
            throw new InvalidOperationException("Only the provisioned dedicated Docker provider is supported.");
    }

    public static void ValidateIdentity(ClusterIdentity state)
    {
        if (!ClusterName().IsMatch(state.Name) || string.IsNullOrWhiteSpace(state.DaemonId))
            throw new InvalidOperationException("Invalid project-owned cluster identity.");
    }

    public static void ValidateKubeconfig(string json, string clusterName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var expected = "kind-" + clusterName;
        if (root.GetProperty("current-context").GetString() != expected)
            throw new InvalidOperationException("Unexpected kubeconfig context.");
        foreach (var key in new[] { "clusters", "contexts", "users" })
            if (root.GetProperty(key).GetArrayLength() != 1 || root.GetProperty(key)[0].GetProperty("name").GetString() != expected)
                throw new InvalidOperationException("Kubeconfig must contain only the recorded kind identity.");
        var context = root.GetProperty("contexts")[0].GetProperty("context");
        if (context.GetProperty("cluster").GetString() != expected || context.GetProperty("user").GetString() != expected)
            throw new InvalidOperationException("Kubeconfig context points to a different identity.");
        var cluster = root.GetProperty("clusters")[0].GetProperty("cluster");
        if (!Uri.TryCreate(cluster.GetProperty("server").GetString(), UriKind.Absolute, out var server) ||
            server.Scheme != "https" || server.Host != "127.0.0.1" || server.Port <= 0 ||
            server.AbsolutePath != "/" || server.UserInfo != "" || server.Query != "" || server.Fragment != "")
            throw new InvalidOperationException("The test API must use HTTPS on runner loopback.");
        foreach (var property in cluster.EnumerateObject())
            if (property.Name is not ("server" or "certificate-authority-data"))
                throw new InvalidOperationException("Unexpected kubeconfig transport settings.");
        ValidateCertificateData(cluster, "certificate-authority-data");
        var user = root.GetProperty("users")[0].GetProperty("user");
        foreach (var property in user.EnumerateObject())
            if (property.Name is not ("client-certificate-data" or "client-key-data"))
                throw new InvalidOperationException("External credentials and exec plugins are forbidden.");
        ValidateCertificateData(user, "client-certificate-data");
        ValidateCertificateData(user, "client-key-data");
    }

    public static string ValidateNode(string json, ClusterIdentity state)
    {
        ValidateIdentity(state);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetArrayLength() != 1)
            throw new InvalidOperationException("Expected exactly one recorded kind node.");
        var node = document.RootElement[0];
        var labels = node.GetProperty("Config").GetProperty("Labels");
        var id = node.GetProperty("Id").GetString()!;
        if (node.GetProperty("Name").GetString() != "/" + state.Name + "-control-plane" ||
            labels.GetProperty("io.x-k8s.kind.cluster").GetString() != state.Name ||
            labels.GetProperty("io.x-k8s.kind.role").GetString() != "control-plane" ||
            !ContainerId().IsMatch(id) || (state.NodeId is not null && state.NodeId != id))
            throw new InvalidOperationException("Runtime node identity differs from the project-owned cluster.");
        return id;
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static void ValidateNetworkMtu(string json, int underlayMtu)
    {
        if (underlayMtu < 1280) throw new InvalidOperationException("Invalid runner network MTU.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetArrayLength() == 0) throw new InvalidOperationException("Missing dedicated runtime network.");
        foreach (var network in document.RootElement.EnumerateArray())
        {
            var name = network.GetProperty("Name").GetString();
            if (name is not ("bridge" or "kind")) throw new InvalidOperationException("Unexpected runtime network identity.");
            if (!network.GetProperty("Options").TryGetProperty("com.docker.network.driver.mtu", out var value) ||
                !int.TryParse(value.GetString(), out var mtu) || mtu < 1280 || mtu > underlayMtu)
                throw new InvalidOperationException($"Dedicated Docker network {name} needs an explicit MTU no greater than runner MTU {underlayMtu}. Fix daemon bridge/default-network MTU and recreate unused networks; do not bypass this prerequisite.");
        }
    }

    private static void ValidateCertificateData(JsonElement element, string name)
    {
        if (Convert.FromBase64String(element.GetProperty(name).GetString()!).Length == 0)
            throw new InvalidOperationException("Kubeconfig must contain inline certificate credentials.");
    }

    [GeneratedRegex("^haso-dev-[a-f0-9]{16}$")]
    private static partial Regex ClusterName();
    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex ContainerId();
}
