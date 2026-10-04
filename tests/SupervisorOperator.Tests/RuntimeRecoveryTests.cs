using System.Text.Json.Nodes;
using DevGuard;

namespace SupervisorOperator.Tests;

public sealed class RuntimeRecoveryTests
{
    private static readonly ClusterIdentity State = new("haso-dev-0123456789abcdef", "old", new string('a', 64), "sealed-config", new string('b', 64));
    private static JsonObject Node(string role, string id) => new() { ["Id"] = id, ["Name"] = "/" + State.Name + "-" + role,
        ["Config"] = new JsonObject { ["Labels"] = new JsonObject { ["io.x-k8s.kind.cluster"] = State.Name, ["io.x-k8s.kind.role"] = role } } };
    [Fact]
    public void RestartWithNoNodesRequiresArchivingAndFreshTests() => Assert.Equal("lost", IsolationGuard.RuntimeRecovery("[]", State, "new"));
    [Fact]
    public void RestartWithIdenticalSealedNodesRetainsTheCluster() => Assert.Equal("retained", IsolationGuard.RuntimeRecovery(
        new JsonArray(Node("control-plane", State.NodeId!), Node("worker", State.WorkerId!)).ToJsonString(), State, "new"));
    [Fact]
    public void PartialNodesNeverCountAsCleanReset() => Assert.Throws<InvalidOperationException>(() => IsolationGuard.RuntimeRecovery(
        new JsonArray(Node("control-plane", State.NodeId!)).ToJsonString(), State, "new"));
    [Fact]
    public void ReplacementNodesCannotBeAdoptedAfterRestart() => Assert.Throws<InvalidOperationException>(() => IsolationGuard.RuntimeRecovery(
        new JsonArray(Node("control-plane", State.NodeId!), Node("worker", new string('c', 64))).ToJsonString(), State, "new"));
    [Fact]
    public void RelabelledSurvivorCannotBeClassifiedAsLost()
    {
        var primary = Node("control-plane", State.NodeId!);
        primary["Config"]!["Labels"]!["io.x-k8s.kind.cluster"] = "other-cluster";
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.RuntimeRecovery(
            new JsonArray(primary, Node("worker", State.WorkerId!)).ToJsonString(), State, "new"));
    }
    [Fact]
    public void RenamedSurvivorCannotBeClassifiedAsLost()
    {
        var primary = Node("control-plane", State.NodeId!);
        primary["Name"] = "/renamed";
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.RuntimeRecovery(
            new JsonArray(primary, Node("worker", State.WorkerId!)).ToJsonString(), State, "new"));
    }
}
