using System.Text.Json.Nodes;
using DevGuard;

namespace SupervisorOperator.Tests;

public sealed class IsolationGuardTests
{
    [Theory]
    [InlineData("bridge", "1500")]
    [InlineData("kind", "1500")]
    [InlineData("bridge", "0")]
    [InlineData("kind", "invalid")]
    public void RuntimeNetworkCannotExceedRunnerMtu(string name, string mtu)
    {
        var json = new JsonArray(new JsonObject
        {
            ["Name"] = name,
            ["Options"] = new JsonObject { ["com.docker.network.driver.mtu"] = mtu }
        }).ToJsonString();
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateNetworkMtu(json, 1450));
    }

    [Fact]
    public void CorrectExplicitRuntimeMtuIsAccepted()
    {
        IsolationGuard.ValidateNetworkMtu("[{\"Name\":\"bridge\",\"Options\":{\"com.docker.network.driver.mtu\":\"1450\"}},{\"Name\":\"kind\",\"Options\":{\"com.docker.network.driver.mtu\":\"1450\"}}]", 1450);
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateNetworkMtu("[{\"Name\":\"bridge\",\"Options\":{}}]", 1450));
    }
    private const string Name = "haso-dev-0123456789abcdef";
    private static JsonObject Configuration()
    {
        var identity = "kind-" + Name;
        return new JsonObject
        {
            ["current-context"] = identity,
            ["clusters"] = new JsonArray(new JsonObject
            {
                ["name"] = identity,
                ["cluster"] = new JsonObject { ["server"] = "https://127.0.0.1:43123", ["certificate-authority-data"] = "Y2E=" }
            }),
            ["contexts"] = new JsonArray(new JsonObject
            {
                ["name"] = identity,
                ["context"] = new JsonObject { ["cluster"] = identity, ["user"] = identity }
            }),
            ["users"] = new JsonArray(new JsonObject
            {
                ["name"] = identity,
                ["user"] = new JsonObject { ["client-certificate-data"] = "Y2VydA==", ["client-key-data"] = "a2V5" }
            })
        };
    }

    [Fact]
    public void DedicatedLoopbackConfigurationIsAccepted() => IsolationGuard.ValidateKubeconfig(Configuration().ToJsonString(), Name);

    [Theory]
    [InlineData("https://10.0.0.1:6443")]
    [InlineData("https://kubernetes.default.svc")]
    [InlineData("http://127.0.0.1:6443")]
    [InlineData("https://127.0.0.1.evil.example:6443")]
    [InlineData("https://user:password@127.0.0.1:6443")]
    [InlineData("https://127.0.0.1:6443/proxy")]
    public void ExternalOrUnexpectedApiEndpointsAreRejected(string server)
    {
        var config = Configuration();
        config["clusters"]![0]!["cluster"]!["server"] = server;
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateKubeconfig(config.ToJsonString(), Name));
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("auth-provider")]
    [InlineData("token")]
    [InlineData("client-key")]
    public void ExternalCredentialSourcesAreRejected(string field)
    {
        var config = Configuration();
        config["users"]![0]!["user"]![field] = new JsonObject { ["command"] = "must-never-execute" };
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateKubeconfig(config.ToJsonString(), Name));
    }

    [Fact]
    public void AnotherKindClusterIsRejected() => Assert.Throws<InvalidOperationException>(() =>
        IsolationGuard.ValidateKubeconfig(Configuration().ToJsonString(), "haso-dev-fedcba9876543210"));

    [Fact]
    public void InsecureTransportIsRejected()
    {
        var config = Configuration();
        config["clusters"]![0]!["cluster"]!["insecure-skip-tls-verify"] = true;
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateKubeconfig(config.ToJsonString(), Name));
    }

    [Theory]
    [InlineData("KUBECONFIG", "/tmp/external-config")]
    [InlineData("USE_EXISTING_CLUSTER", "true")]
    [InlineData("USE_EXISTING_CLUSTER", "false")]
    [InlineData("DOCKER_HOST", "tcp://remote.example:2375")]
    [InlineData("DOCKER_HOST", "unix:///var/run/docker.sock")]
    [InlineData("DOCKER_CONTEXT", "production")]
    [InlineData("CONTAINER_HOST", "ssh://remote.example")]
    [InlineData("KIND_EXPERIMENTAL_PROVIDER", "podman")]
    public void PoisonedAmbientConfigurationIsRejected(string key, string value) => Assert.Throws<InvalidOperationException>(() =>
        IsolationGuard.ValidateEnvironment(new Dictionary<string, string?> { [key] = value }));

    [Fact]
    public void ProvisionedEndpointIsAccepted() => IsolationGuard.ValidateEnvironment(new Dictionary<string, string?>
    {
        ["DOCKER_HOST"] = IsolationGuard.RuntimeEndpoint, ["KIND_EXPERIMENTAL_PROVIDER"] = "docker"
    });

    [Fact]
    public void ReplacementNodeCannotBeUsedForCleanup()
    {
        var state = new ClusterIdentity(Name, "dedicated-daemon", new string('a', 64));
        var nodes = new JsonArray(new JsonObject
        {
            ["Id"] = new string('b', 64), ["Name"] = "/" + Name + "-control-plane",
            ["Config"] = new JsonObject { ["Labels"] = new JsonObject
            {
                ["io.x-k8s.kind.cluster"] = Name, ["io.x-k8s.kind.role"] = "control-plane"
            } }
        });
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateNode(nodes.ToJsonString(), state));
    }
}
