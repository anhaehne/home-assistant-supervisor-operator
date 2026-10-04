using System.Text.Json.Nodes;
using DevGuard;

namespace SupervisorOperator.Tests;

public sealed class WorkerIsolationTests
{
    [Fact]
    public void AReplacementWorkerCannotBeUsedForFaultInjectionOrCleanup()
    {
        var state = new ClusterIdentity("haso-dev-0123456789abcdef", "daemon", WorkerId: new string('a', 64));
        var workers = new JsonArray(new JsonObject { ["Id"] = new string('b', 64), ["Name"] = "/" + state.Name + "-worker",
            ["Config"] = new JsonObject { ["Labels"] = new JsonObject { ["io.x-k8s.kind.cluster"] = state.Name, ["io.x-k8s.kind.role"] = "worker" } } });
        Assert.Throws<InvalidOperationException>(() => IsolationGuard.ValidateWorker(workers.ToJsonString(), state));
    }
}
