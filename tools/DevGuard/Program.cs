using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using DevGuard;

try
{
    var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
    IsolationGuard.ValidateEnvironment(environment);
    if (args is ["preflight"]) return 0;
    if (args is ["network-mtu", var underlay])
    {
        IsolationGuard.ValidateNetworkMtu(Console.In.ReadToEnd(), int.Parse(underlay));
        return 0;
    }
    if (args is ["redact", var stateDirectory])
    {
        var content = Console.In.ReadToEnd();
        foreach (var filename in new[] { "core-token", "gateway-token" })
        {
            var tokenPath = Path.Combine(stateDirectory, filename);
            if (File.Exists(tokenPath))
            {
                var token = File.ReadAllText(tokenPath).Trim();
                if (token.Length > 0) content = content.Replace(token, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        var credentials = Path.Combine(stateDirectory, "browser-credentials.json");
        if (File.Exists(credentials))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentials));
            var password = document.RootElement.GetProperty("Password").GetString();
            if (!string.IsNullOrEmpty(password)) content = content.Replace(password, "[REDACTED]", StringComparison.Ordinal);
        }
        Console.Write(content);
        return 0;
    }
    if (args is ["init", var statePath, var daemonId])
    {
        var identity = new ClusterIdentity("haso-dev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), daemonId);
        using var stream = new FileStream(statePath, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, identity);
        return 0;
    }
    if (args.Length < 2) throw new InvalidOperationException("Missing guard command or state file.");
    var path = args[1];
    var state = JsonSerializer.Deserialize<ClusterIdentity>(File.ReadAllText(path))!;
    IsolationGuard.ValidateIdentity(state);
    switch (args[0])
    {
        case "name":
            Console.WriteLine(state.Name);
            break;
        case "node-id":
            Console.WriteLine(state.NodeId ?? "");
            break;
        case "worker-id":
            Console.WriteLine(state.WorkerId ?? "");
            break;
        case "core-node":
            Console.WriteLine(state.Name + (state.WorkerId is null ? "-control-plane" : "-worker"));
            break;
        case "has-worker":
            Console.WriteLine(state.WorkerId is not null ? "true" : "false");
            break;
        case "worker":
        case "seal-worker":
            var workerId = IsolationGuard.ValidateWorker(Console.In.ReadToEnd(), state);
            if (args[0] == "seal-worker") Save(state with { WorkerId = workerId });
            else if (state.WorkerId is null) throw new InvalidOperationException("Worker identity has not been recorded");
            break;
        case "runtime":
            if (args.Length != 3 || args[2] != state.DaemonId)
                throw new InvalidOperationException("The dedicated runtime changed; refusing cluster access or cleanup.");
            break;
        case "recover-runtime":
            if (args.Length != 3) throw new InvalidOperationException("Missing replacement daemon identity");
            var recovery = IsolationGuard.RuntimeRecovery(Console.In.ReadToEnd(), state, args[2]);
            if (recovery == "retained") Save(state with { DaemonId = args[2] });
            Console.WriteLine(recovery);
            break;
        case "node":
        case "seal-node":
            var nodeId = IsolationGuard.ValidateNode(Console.In.ReadToEnd(), state);
            if (args[0] == "seal-node") Save(state with { NodeId = nodeId });
            else if (state.NodeId is null) throw new InvalidOperationException("Node identity has not been recorded.");
            break;
        case "seal-config":
        case "config":
            if (args.Length != 3) throw new InvalidOperationException("Missing kubeconfig path.");
            var raw = File.ReadAllText(args[2]);
            IsolationGuard.ValidateKubeconfig(Console.In.ReadToEnd(), state.Name);
            var hash = IsolationGuard.Hash(raw);
            if (args[0] == "seal-config")
            {
                if (state.KubeconfigHash is not null) throw new InvalidOperationException("Kubeconfig identity is already sealed.");
                Save(state with { KubeconfigHash = hash });
            }
            else if (hash != state.KubeconfigHash)
                throw new InvalidOperationException("Kubeconfig changed after cluster creation; refusing any API request.");
            break;
        default:
            throw new InvalidOperationException("Unknown development guard command.");
    }
    return 0;
    void Save(ClusterIdentity identity)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(identity));
        File.Move(temporary, path, overwrite: true);
    }
}
catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or FormatException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine("Development isolation check failed: " + exception.Message);
    return 1;
}
