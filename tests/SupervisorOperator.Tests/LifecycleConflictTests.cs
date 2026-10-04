using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using SupervisorOperator.Lifecycle;
using Supervisor.Contracts;

namespace SupervisorOperator.Tests;

public sealed class LifecycleConflictTests
{
    [Fact]
    public async Task PeriodicResyncRequeuesAfterFrameworkGetFailuresWithoutAWatchEvent()
    {
        using var environment = new Fixture();
        environment.Handler.InstanceReadFailures = 6;
        var queued = 0;
        KubeOps.Abstractions.Reconciliation.Queue.EntityQueue<HomeAssistantInstance> queue = (instance, type, source, delay, retry, cancellation) =>
        {
            Assert.Equal("instance", instance.Metadata.Uid);
            Assert.Equal("test-install", instance.Metadata.NamespaceProperty);
            Assert.Equal(0, retry);
            queued++;
            return Task.FromResult(true);
        };
        for (var attempt = 0; attempt < 6; attempt++)
            await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() => InstanceResync.Reschedule(environment.Lifecycle, queue, CancellationToken.None));
        Assert.Equal(0, queued);
        Assert.True(await InstanceResync.Reschedule(environment.Lifecycle, queue, CancellationToken.None));
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task DependencyRecoveryResumesAfterMoreThanTheFrameworkRetryBudget()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Accepted");
        environment.Handler.MissingTemplateReads = 6;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var result = await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(TimeSpan.FromSeconds(5), result.RequeueAfter);
            Assert.Equal("Accepted", environment.Handler.Operation.Status.Phase);
            Assert.Equal("ReconciliationBlocked", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        }
        var resumed = await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.True(resumed.IsSuccess);
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
    }

    [Fact]
    public async Task DistinctKeyCannotBeAcknowledgedWithoutDurableAcceptance()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Starting");
        await Assert.ThrowsAsync<ApiValidationException>(() => environment.Lifecycle.Accept("Start", "distinct-key", CancellationToken.None));
        Assert.Equal(0, environment.Handler.Creations);
    }

    [Fact]
    public async Task GitOpsDoesNotReplayThePreviousUiRestart()
    {
        using var environment = new Fixture();
        environment.Instance.Spec.Ownership = "GitOps";
        environment.Instance.Spec.Command!.Action = "Restart";
        environment.Instance.Spec.Command.RequestKey = "previous-ui";
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Reconcile", environment.Handler.Operation!.Spec.Action);
        Assert.Null(environment.Handler.Operation.Metadata.Annotations);
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
    }

    [Fact]
    public async Task OrphanedPodBlocksWorkloadRecreation()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Starting");
        environment.Handler.Pod = new V1Pod { Metadata = new() { Uid = "surviving-pod" }, Spec = new() { NodeName = "selected-node" } };
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
        Assert.Equal("FenceRequired", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
    }

    [Fact]
    public async Task OrphanedPodBlocksFinalizationUntilItsVerifiedAbsence()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Starting");
        environment.Handler.Pod = new V1Pod { Metadata = new() { Uid = "surviving-pod" }, Spec = new() { NodeName = "selected-node" } };
        Assert.False(await environment.Lifecycle.Finalize(environment.Instance, CancellationToken.None));
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
        Assert.Equal("FenceRequired", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        environment.Handler.Pod = null;
        Assert.True(await environment.Lifecycle.Finalize(environment.Instance, CancellationToken.None));
        Assert.Equal("Failed", environment.Handler.Operation.Status.Phase);
    }

    [Fact]
    public async Task ForeignPodBlocksFinalizationWithoutWorkloadMutation()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Starting");
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        environment.Handler.Pod = OwnedPod(environment.Instance, "old-workload");
        Assert.False(await environment.Lifecycle.Finalize(environment.Instance, CancellationToken.None));
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
        Assert.Equal(1, environment.Handler.Workload.Spec.Replicas);
        Assert.Equal("FenceRequired", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
    }

    [Fact]
    public async Task ReadyPodOwnedByAnotherStatefulSetCannotPublishSuccess()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        environment.Handler.Pod = OwnedPod(environment.Instance, "old-workload");
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("FenceRequired", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        Assert.Equal("False", environment.Handler.PublishedInstance.Status.Conditions.Single().Status);
    }

    [Theory]
    [InlineData("initial-node")]
    [InlineData("recovery-node")]
    public async Task ReadyPodOwnedByCurrentInstanceAndStatefulSetPublishesSuccess(string node)
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Spec.NodeName = node;
        environment.Handler.Workload.Metadata.Annotations[CoreLifecycle.TemplateHash] = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ConflictApi.TemplateYaml)));
        environment.Instance.Status.PodUid = environment.Handler.Pod.Metadata.Uid;
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Succeeded", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        Assert.Equal("True", environment.Handler.PublishedInstance.Status.Conditions.Single().Status);
    }

    [Fact]
    public async Task AdoptedPodFromAnotherInstanceCannotPublishSuccess()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Metadata.Annotations[CoreLifecycle.Owner] = "old-instance";
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("FenceRequired", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
    }

    private static V1StatefulSet OwnedWorkload(HomeAssistantInstance instance) => new() { Metadata = new()
        { Name = "core", NamespaceProperty = instance.Metadata.NamespaceProperty, ResourceVersion = "1",
            Uid = "current-workload", Annotations = new Dictionary<string, string> { [CoreLifecycle.Owner] = instance.Metadata.Uid } },
        Spec = new() { Replicas = 1, Template = new() { Spec = new() { Containers = [new V1Container { Name = "core", Image = "fixture" }] } } } };

    private static V1Pod OwnedPod(HomeAssistantInstance instance, string workloadUid) => new() { Metadata = new()
        { Uid = "current-pod", Annotations = new Dictionary<string, string> { [CoreLifecycle.Owner] = instance.Metadata.Uid },
            OwnerReferences = [new V1OwnerReference { Controller = true, Kind = "StatefulSet", Name = "core", Uid = workloadUid, ApiVersion = "apps/v1" }] },
        Spec = new() { NodeName = "selected-node" }, Status = new() { Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }] } };

    [Fact]
    public async Task UnscheduledPodReachesTheStartupDeadline()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("WaitingForHealth");
        environment.Handler.Operation.Status.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Spec.NodeName = null;
        environment.Handler.Pod.Status = new() { Phase = "Pending" };
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Failed", environment.Handler.Operation.Status.Phase);
        Assert.Equal("Failed", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
    }

    [Fact]
    public async Task TerminalFailureRepairsProjectionAfterAnInterruptedStatusWrite()
    {
        using var environment = new Fixture();
        environment.Handler.Operation = environment.Operation("Failed");
        environment.Handler.Operation.Status.Error = "Startup timed out";
        environment.Instance.Status.Conditions = [new() { Type = "Ready", Status = "False", Reason = "WaitingForHealth" }];
        await environment.Controller.ReconcileAsync(environment.Instance, CancellationToken.None);
        Assert.Equal("Failed", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        Assert.Equal("Startup timed out", environment.Handler.PublishedInstance.Status.Conditions.Single().Message);
    }

    private static Fixture ProxyFixture()
    {
        var environment = new Fixture();
        environment.Instance.Spec.HttpProxy = new HttpProxySettings(["10.244.69.17/32"]);
        environment.Handler.Workload = OwnedWorkload(environment.Instance);
        environment.Handler.Workload.Metadata.ResourceVersion = "4";
        environment.Handler.Workload.Metadata.Annotations[CoreLifecycle.TemplateHash] = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ConflictApi.TemplateYaml)));
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Metadata.Uid = "old-core-pod";
        environment.Handler.Operation = environment.Operation("WaitingForHealth");
        return environment;
    }

    [Fact]
    public async Task ProxyInstallationPersistsRestartOwnershipBeforeNativeConfiguration()
    {
        using var environment = ProxyFixture();
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal("ApplyingHttpConfiguration", environment.Handler.Operation!.Status.Phase);
        Assert.Equal("intended-http-fingerprint", environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Equal("old-core-pod", environment.Handler.Operation.Status.HttpProxyStagedPodUid);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Empty(environment.Handler.ReplicaWrites);
    }

    [Theory]
    [InlineData("Ui")]
    [InlineData("GitOps")]
    public async Task NativeProxyRestartCallbackJoinsPersistedOperationWithoutChangingIntent(string ownership)
    {
        using var environment = ProxyFixture();
        environment.Instance.Spec.Ownership = ownership;
        environment.Handler.Operation = environment.Operation("ApplyingHttpConfiguration");
        environment.Handler.Operation.Status.HttpProxyFingerprint = "intended-http-fingerprint";
        var command = environment.Instance.Spec.Command;
        var id = await environment.Lifecycle.Accept("Restart", null, default);
        Assert.Equal(environment.Handler.Operation.Spec.Id, id);
        Assert.Same(command, environment.Instance.Spec.Command);
        Assert.Equal("Running", environment.Instance.Spec.DesiredState);
        Assert.Equal(ownership, environment.Instance.Spec.Ownership);
        Assert.Equal(0, environment.Handler.IntentWrites);
        Assert.Equal(0, environment.Handler.Creations);
    }

    [Fact]
    public async Task ProxyTrialIsConfirmedOnlyAfterFencedReplacementAndApplicationHealth()
    {
        using var environment = ProxyFixture();
        await environment.Lifecycle.Step(environment.Instance, default);
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal("Stopping", environment.Handler.Operation!.Status.Phase);
        Assert.Equal(1, environment.Native.StageCalls);
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal(0, environment.Handler.Workload!.Spec.Replicas);
        Assert.Equal("Stopping", environment.Handler.Operation.Status.Phase);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal(new[] { 0 }, environment.Handler.ReplicaWrites);
        Assert.Equal("old-core-pod", environment.Handler.Pod!.Metadata.Uid);
        environment.Handler.Pod = null;
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal(new[] { 0, 1 }, environment.Handler.ReplicaWrites);
        Assert.Equal("WaitingForHealth", environment.Handler.Operation.Status.Phase);
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Metadata.Uid = "replacement-core-pod";
        environment.Native.State = environment.Native.State with { PendingActive = true };
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal("Succeeded", environment.Handler.Operation.Status.Phase);
        Assert.Equal(1, environment.Native.ConfirmCalls);
        Assert.Equal("intended-http-fingerprint", environment.Native.ConfirmedFingerprint);
        Assert.Null(environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Null(environment.Handler.Operation.Status.HttpProxyStagedPodUid);
    }

    [Fact]
    public async Task LostNativeStageResponseRetriesPersistedIntentWithoutPrematureConfirmation()
    {
        using var environment = ProxyFixture();
        await environment.Lifecycle.Step(environment.Instance, default);
        environment.Native.LoseNextStageResponse = true;
        var interrupted = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.False(interrupted.IsSuccess);
        Assert.Equal("ApplyingHttpConfiguration", environment.Handler.Operation!.Status.Phase);
        Assert.Equal("intended-http-fingerprint", environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Equal("old-core-pod", environment.Handler.Operation.Status.HttpProxyStagedPodUid);
        Assert.True(environment.Native.State.PendingExists);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Empty(environment.Handler.ReplicaWrites);
        var resumed = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(resumed.IsSuccess);
        Assert.Equal("Stopping", environment.Handler.Operation.Status.Phase);
        Assert.Equal(0, environment.Native.ConfirmCalls);
    }

    [Fact]
    public async Task TerminalOperationChecksApplicationHealthBeforeManagingProxyDrift()
    {
        using var environment = ProxyFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.PodUid = environment.Handler.Pod!.Metadata.Uid;
        environment.Native.CoreState = "STARTING";
        await environment.Lifecycle.Step(environment.Instance, default);
        Assert.Equal("WaitingForHealth", environment.Handler.Operation.Status.Phase);
        Assert.Null(environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
    }

    [Fact]
    public async Task NewProxyPolicyCreatesFreshIntentAfterHistoricalUiFailure()
    {
        using var environment = ProxyFixture();
        environment.Instance.Spec.HttpProxy = null;
        environment.Instance.Spec.Command!.Action = "Restart";
        environment.Instance.Spec.Command.RequestKey = "historical-ui-request";
        var failed = environment.Operation("Failed");
        failed.Status.Error = "Historical restart failed";
        environment.Handler.Operation = failed;
        var historicalId = failed.Spec.Id;
        environment.Instance.Spec.HttpProxy = new(["10.244.69.17/32"]);
        environment.Instance.Metadata.Generation = 2;
        var newId = CoreLifecycle.CommandId(environment.Instance);
        Assert.NotEqual(historicalId, newId);
        var accepted = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(accepted.IsSuccess);
        Assert.Equal("Accepted", environment.Handler.Operation!.Status.Phase);
        Assert.Equal(newId, environment.Handler.Operation.Spec.Id);
        Assert.Equal(2, environment.Handler.Operation.Spec.TargetGeneration);
        Assert.Equal("Reconcile", environment.Handler.Operation.Spec.Action);
        Assert.Null(environment.Handler.Operation.Metadata.Annotations);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("Starting", environment.Handler.Operation.Status.Phase);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("ApplyingHttpConfiguration", environment.Handler.Operation.Status.Phase);
        Assert.Equal("intended-http-fingerprint", environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Equal("Failed", failed.Status.Phase);
        Assert.Equal("Historical restart failed", failed.Status.Error);
        Assert.Contains(failed, environment.Handler.HistoricalOperations);
        Assert.Equal(1, environment.Handler.Creations);
    }

    [Fact]
    public async Task UnchangedFailedProxyIntentIsNotRetriedOrAcknowledgedAsSuccess()
    {
        using var environment = ProxyFixture();
        environment.Handler.Operation = environment.Operation("Failed");
        environment.Handler.Operation.Status.Error = "Native proxy trial failed";
        var id = CoreLifecycle.CommandId(environment.Instance);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Failed", environment.Handler.Operation.Status.Phase);
        Assert.Equal("Native proxy trial failed", environment.Handler.Operation.Status.Error);
        Assert.Equal(0, environment.Handler.Creations);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Empty(environment.Handler.ReplicaWrites);
    }

    [Fact]
    public async Task ReturningToPreviouslyFailedProxyPolicyCreatesANewGenerationIntent()
    {
        using var environment = ProxyFixture();
        var failedA = environment.Operation("Failed");
        failedA.Status.Error = "Policy A trial failed";
        environment.Handler.Operation = failedA;
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(environment.Instance.Status.HasManagedHttpProxy);
        environment.Instance.Spec.HttpProxy = new(["10.244.53.213/32"]);
        environment.Instance.Metadata.Generation = 2;
        var policyB = CoreLifecycle.CommandId(environment.Instance);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(policyB, environment.Handler.Operation!.Spec.Id);
        environment.Instance.Spec.HttpProxy = new(["10.244.69.17/32"]);
        environment.Instance.Metadata.Generation = 3;
        var returnedA = CoreLifecycle.CommandId(environment.Instance);
        Assert.NotEqual(failedA.Spec.Id, returnedA);
        Assert.NotEqual(policyB, returnedA);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(returnedA, environment.Handler.Operation.Spec.Id);
        Assert.Equal(3, environment.Handler.Operation.Spec.TargetGeneration);
        Assert.Equal("Accepted", environment.Handler.Operation.Status.Phase);
        Assert.Equal("Reconcile", environment.Handler.Operation.Spec.Action);
        Assert.Equal("Failed", failedA.Status.Phase);
        Assert.Equal("Policy A trial failed", failedA.Status.Error);
        Assert.Equal(2, environment.Handler.Creations);
    }

    [Fact]
    public async Task ReleasingProxyManagementDoesNotReviveHistoricalUnmanagedUiFailure()
    {
        using var environment = ProxyFixture();
        environment.Instance.Spec.HttpProxy = null;
        var failedUnmanaged = environment.Operation("Failed");
        environment.Handler.Operation = failedUnmanaged;
        environment.Instance.Spec.HttpProxy = new(["10.244.69.17/32"]);
        environment.Instance.Metadata.Generation = 2;
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(environment.Instance.Status.HasManagedHttpProxy);
        Assert.True(environment.Handler.PublishedInstance!.Status.HasManagedHttpProxy);
        var managedId = environment.Handler.Operation!.Spec.Id;
        environment.Instance.Spec.HttpProxy = null;
        environment.Instance.Metadata.Generation = 3;
        var unmanagedId = CoreLifecycle.CommandId(environment.Instance);
        Assert.NotEqual(failedUnmanaged.Spec.Id, unmanagedId);
        Assert.NotEqual(managedId, unmanagedId);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(unmanagedId, environment.Handler.Operation.Spec.Id);
        Assert.Equal(3, environment.Handler.Operation.Spec.TargetGeneration);
        Assert.Equal("Accepted", environment.Handler.Operation.Status.Phase);
        Assert.True(environment.Instance.Status.HasManagedHttpProxy);
        Assert.Equal("Failed", failedUnmanaged.Status.Phase);
    }

    [Fact]
    public async Task AcceptedUiCommandUsesItsApiIdForTheAcceptedManagedGeneration()
    {
        using var environment = ProxyFixture();
        environment.Instance.Status.HasManagedHttpProxy = true;
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.OperationId = environment.Handler.Operation.Spec.Id;
        environment.Handler.AllowIntentWrites = true;
        var id = await environment.Lifecycle.Accept("Restart", "managed-ui-restart", default);
        Assert.Equal(2, environment.Instance.Metadata.Generation);
        Assert.Equal(2, environment.Instance.Spec.Command!.AcceptedGeneration);
        Assert.Equal(id, environment.Instance.Spec.Command.Id);
        Assert.Equal(id, CoreLifecycle.CommandId(environment.Instance));
        Assert.Equal(1, environment.Handler.IntentWrites);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(id, environment.Handler.Operation!.Spec.Id);
        Assert.Equal("Restart", environment.Handler.Operation.Spec.Action);
        Assert.Equal("managed-ui-restart", environment.Handler.Operation.Metadata.Annotations["ha-operator.io/request-key"]);
    }

    private static Fixture PodNetworkFixture()
    {
        var environment = ProxyFixture();
        environment.Instance.Spec.TrustPodNetwork = true;
        environment.Instance.Status.HasManagedHttpProxy = true;
        environment.Instance.Status.DiscoveredPodNetworks = ["10.244.1.0/24", "10.244.2.0/24"];
        environment.Instance.Status.PodNetworkRevision = 1;
        environment.Handler.Operation = environment.Operation("WaitingForHealth");
        environment.Native.ExpectedTrustedProxies = ["10.244.1.0/24", "10.244.2.0/24", "10.244.69.17/32"];
        return environment;
    }

    [Fact]
    public async Task DiscoveredNetworkChangesAndRevertsCreateNewIntentsWithoutSpecGenerationChanges()
    {
        using var environment = PodNetworkFixture();
        var failedA = environment.Operation("Failed");
        failedA.Status.Error = "Original network trial failed";
        environment.Handler.Operation = failedA;
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        environment.Handler.Nodes = [PodNetworkNode("worker-new", "10.244.3.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        var changedId = environment.Handler.Operation!.Spec.Id;
        Assert.Equal("False", environment.Handler.PublishedInstance!.Status.Conditions.Single().Status);
        Assert.Equal("PodNetworkChanged", environment.Handler.PublishedInstance.Status.Conditions.Single().Reason);
        Assert.Equal(2, environment.Instance.Status.PodNetworkRevision);
        Assert.NotEqual(failedA.Spec.Id, changedId);
        environment.Handler.Nodes = [PodNetworkNode("worker-a", "10.244.1.0/24"), PodNetworkNode("worker-b", "10.244.2.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(3, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(1, environment.Instance.Metadata.Generation);
        Assert.NotEqual(changedId, environment.Handler.Operation.Spec.Id);
        Assert.NotEqual(failedA.Spec.Id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Accepted", environment.Handler.Operation.Status.Phase);
        Assert.Equal("Failed", failedA.Status.Phase);
        Assert.Equal("Original network trial failed", failedA.Status.Error);
        Assert.Equal(2, environment.Handler.Creations);
    }

    [Fact]
    public async Task UnchangedDiscoveredNetworksDoNotCreateJobsOrRestartCore()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Handler.Operation.Status.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var completedAt = environment.Handler.Operation.Status.CompletedAt;
        var id = environment.Handler.Operation.Spec.Id;
        environment.Instance.Status.PodUid = environment.Handler.Pod!.Metadata.Uid;
        environment.Native.State = environment.Native.State with { StableMatches = true };
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(1, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal(completedAt, environment.Handler.Operation.Status.CompletedAt);
        Assert.Equal(0, environment.Handler.Creations);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Empty(environment.Handler.ReplicaWrites);
        Assert.Equal(2, environment.Handler.NodeReads);
    }

    [Fact]
    public async Task GatewayReceivesNormalizedUnionOfExplicitTrustAndDeclaredPodNetworks()
    {
        using var environment = PodNetworkFixture();
        environment.Instance.Spec.HttpProxy = new(["192.0.2.9", "10.244.1.0/24"]);
        environment.Native.ExpectedTrustedProxies = ["10.244.1.0/24", "10.244.2.0/24", "192.0.2.9/32"];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("ApplyingHttpConfiguration", environment.Handler.Operation!.Status.Phase);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("Stopping", environment.Handler.Operation.Status.Phase);
        Assert.Equal(1, environment.Native.StageCalls);
        Assert.Equal(environment.Native.ExpectedTrustedProxies, environment.Native.LastTrustedProxies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCidrsOrNodeReadPermissionFailsClosedWithoutMutatingCore(bool forbidden)
    {
        using var environment = PodNetworkFixture();
        if (forbidden) environment.Handler.NodeReadForbidden = true;
        else environment.Handler.Nodes = [new V1Node { Metadata = new() { Name = "missing-cidr" }, Spec = new() }];
        var id = environment.Handler.Operation!.Spec.Id;
        var result = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(result.IsSuccess);
        Assert.Equal("PodNetworkUnavailable", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        Assert.Equal("False", environment.Handler.PublishedInstance.Status.Conditions.Single().Status);
        Assert.Equal(1, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("WaitingForHealth", environment.Handler.Operation.Status.Phase);
        Assert.Equal(0, environment.Handler.Creations);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Empty(environment.Handler.ReplicaWrites);
    }

    [Fact]
    public async Task DeclaredPodNetworksThatDoNotCoverCoreAddressInvalidateReadyWithoutConfiguration()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.PodUid = environment.Handler.Pod!.Metadata.Uid;
        environment.Instance.Status.Conditions = [new() { Type = "Ready", Status = "True", Reason = "Succeeded" }];
        environment.Handler.Pod.Status.PodIP = "10.244.69.17";
        var id = environment.Handler.Operation.Spec.Id;
        var result = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(result.IsSuccess);
        Assert.Equal("PodNetworkUnavailable", environment.Handler.PublishedInstance!.Status.Conditions.Single().Reason);
        Assert.Equal("False", environment.Handler.PublishedInstance.Status.Conditions.Single().Status);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal(1, environment.Handler.Workload!.Spec.Replicas);
        Assert.Equal("old-core-pod", environment.Handler.Pod.Metadata.Uid);
        Assert.Equal(0, environment.Handler.Creations);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Null(environment.Native.LastTrustedProxies);
        Assert.Empty(environment.Handler.ReplicaWrites);
    }

    [Fact]
    public async Task AcceptedUiCommandIsBoundToObservedPodNetworkRevision()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.OperationId = environment.Handler.Operation.Spec.Id;
        environment.Handler.AllowIntentWrites = true;
        var id = await environment.Lifecycle.Accept("Restart", "discovered-network-ui-restart", default);
        Assert.Equal(id, CoreLifecycle.CommandId(environment.Instance));
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(id, environment.Instance.Status.ProxyCommandId);
        Assert.Equal(1, environment.Instance.Status.ProxyCommandNetworkRevision);
        Assert.Equal(id, environment.Handler.Operation!.Spec.Id);
        Assert.Equal("Restart", environment.Handler.Operation.Spec.Action);
        environment.Handler.Nodes = [PodNetworkNode("worker-new", "10.244.3.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(2, environment.Instance.Status.PodNetworkRevision);
        Assert.NotEqual(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Reconcile", environment.Handler.Operation.Spec.Action);
        Assert.Null(environment.Handler.Operation.Metadata.Annotations);
    }

    [Fact]
    public async Task DiscoveryBetweenApiAcceptanceAndFirstReconcileRetainsAcceptedId()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.OperationId = environment.Handler.Operation.Spec.Id;
        environment.Handler.AllowIntentWrites = true;
        var id = await environment.Lifecycle.Accept("Restart", "network-change-at-acceptance", default);
        environment.Handler.Nodes = [PodNetworkNode("new-worker", "10.244.3.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(2, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(id, environment.Instance.Status.ProxyCommandId);
        Assert.Equal(2, environment.Instance.Status.ProxyCommandNetworkRevision);
        Assert.Equal(id, environment.Handler.Operation!.Spec.Id);
        Assert.Equal("Restart", environment.Handler.Operation.Spec.Action);
        Assert.Equal(id, CoreLifecycle.CommandId(environment.Instance));
        environment.Handler.Operation.Status.Phase = "Succeeded";
        environment.Instance.Status.OperationId = id;
        environment.Instance.Status.PodUid = environment.Handler.Pod!.Metadata.Uid;
        environment.Native.State = environment.Native.State with { StableMatches = true };
        environment.Native.ExpectedTrustedProxies = ["10.244.3.0/24", "10.244.69.17/32"];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Succeeded", environment.Handler.Operation.Status.Phase);
        environment.Handler.Nodes = [PodNetworkNode("later-worker", "10.244.4.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(3, environment.Instance.Status.PodNetworkRevision);
        Assert.NotEqual(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Reconcile", environment.Handler.Operation.Spec.Action);
    }

    [Fact]
    public async Task ChangedNetworksAreDeferredWhilePreparedNativeTrialIsActive()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation!.Status.Phase = "ApplyingHttpConfiguration";
        environment.Handler.Operation.Status.HttpProxyFingerprint = "intended-http-fingerprint";
        environment.Handler.Operation.Status.HttpProxyStagedPodUid = "old-core-pod";
        var id = environment.Handler.Operation.Spec.Id;
        environment.Handler.Nodes = [PodNetworkNode("later-worker", "10.244.3.0/24")];
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(1, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(new[] { "10.244.1.0/24", "10.244.2.0/24" }, environment.Instance.Status.DiscoveredPodNetworks);
        Assert.Equal(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Stopping", environment.Handler.Operation.Status.Phase);
        Assert.Equal(1, environment.Native.StageCalls);
        Assert.Equal(0, environment.Handler.Creations);
        Assert.Empty(environment.Handler.ReplicaWrites);
        environment.Handler.Operation.Status.Phase = "Succeeded";
        environment.Handler.Operation.Status.HttpProxyFingerprint = null;
        environment.Handler.Operation.Status.HttpProxyStagedPodUid = null;
        environment.Native.State = environment.Native.State with { StableMatches = true, PendingExists = false,
            PendingMatches = false, PendingActive = false, PendingFingerprint = null };
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(2, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(new[] { "10.244.3.0/24" }, environment.Instance.Status.DiscoveredPodNetworks);
        Assert.NotEqual(id, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Reconcile", environment.Handler.Operation.Spec.Action);
        Assert.Equal(1, environment.Native.StageCalls);
    }

    [Fact]
    public async Task OwnedReplacementOnNewNodeNetworkFinishesFrozenTrialBeforeApplyingNewNetworks()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation!.Status.HttpProxyFingerprint = "intended-http-fingerprint";
        environment.Handler.Operation.Status.HttpProxyStagedPodUid = "old-core-pod";
        var frozenId = environment.Handler.Operation.Spec.Id;
        environment.Native.State = new(false, true, true, true, false, "intended-http-fingerprint", "intended-http-fingerprint");
        environment.Handler.Nodes = [PodNetworkNode("replacement-worker", "10.244.3.0/24")];
        environment.Handler.Pod = OwnedPod(environment.Instance, "current-workload");
        environment.Handler.Pod.Metadata.Uid = "replacement-core-pod";
        environment.Handler.Pod.Spec.NodeName = "replacement-worker";
        environment.Handler.Pod.Status.PodIP = "10.244.3.17";
        var completed = await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.True(completed.IsSuccess);
        Assert.Equal("Succeeded", environment.Handler.Operation.Status.Phase);
        Assert.Equal(frozenId, environment.Handler.Operation.Spec.Id);
        Assert.Equal(1, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(new[] { "10.244.1.0/24", "10.244.2.0/24" }, environment.Instance.Status.DiscoveredPodNetworks);
        Assert.Equal(1, environment.Native.ConfirmCalls);
        Assert.Equal("intended-http-fingerprint", environment.Native.ConfirmedFingerprint);
        Assert.Null(environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Empty(environment.Handler.ReplicaWrites);

        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(2, environment.Instance.Status.PodNetworkRevision);
        Assert.Equal(new[] { "10.244.3.0/24" }, environment.Instance.Status.DiscoveredPodNetworks);
        Assert.NotEqual(frozenId, environment.Handler.Operation.Spec.Id);
        Assert.Equal("Accepted", environment.Handler.Operation.Status.Phase);
        Assert.Equal("False", environment.Handler.PublishedInstance!.Status.Conditions.Single().Status);
        environment.Native.ExpectedTrustedProxies = ["10.244.3.0/24", "10.244.69.17/32"];
        environment.Native.State = environment.Native.State with { StableMatches = false, DesiredFingerprint = "new-http-fingerprint" };
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("ApplyingHttpConfiguration", environment.Handler.Operation.Status.Phase);
        Assert.Equal("new-http-fingerprint", environment.Handler.Operation.Status.HttpProxyFingerprint);
        Assert.Equal("replacement-core-pod", environment.Handler.Operation.Status.HttpProxyStagedPodUid);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("Stopping", environment.Handler.Operation.Status.Phase);
        Assert.Equal(1, environment.Native.StageCalls);
        Assert.Equal(environment.Native.ExpectedTrustedProxies, environment.Native.LastTrustedProxies);
        Assert.Equal(1, environment.Native.ConfirmCalls);
    }

    [Fact]
    public async Task AcceptedStopUsesCachedNetworksAndCompletesWhenNodeReadsAreDenied()
    {
        using var environment = PodNetworkFixture();
        environment.Handler.Operation = environment.Operation("Succeeded");
        environment.Instance.Status.OperationId = environment.Handler.Operation.Spec.Id;
        environment.Handler.AllowIntentWrites = true;
        environment.Handler.NodeReadForbidden = true;
        var id = await environment.Lifecycle.Accept("Stop", "stop-without-node-permission", default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(id, environment.Handler.Operation!.Spec.Id);
        Assert.Equal("Stop", environment.Handler.Operation.Spec.Action);
        Assert.Equal(id, environment.Instance.Status.ProxyCommandId);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(0, environment.Handler.Workload!.Spec.Replicas);
        environment.Handler.Pod = null;
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("Succeeded", environment.Handler.Operation.Status.Phase);
        // Job completion and instance projection are separate durable writes.
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal("Stopped", environment.Handler.PublishedInstance!.Status.State);
        Assert.Equal(0, environment.Handler.NodeReads);
        Assert.Equal(0, environment.Native.StageCalls);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.Equal(new[] { 0 }, environment.Handler.ReplicaWrites);
    }

    private static V1Node PodNetworkNode(string name, string cidr) => new()
    {
        Metadata = new() { Name = name }, Spec = new() { PodCIDR = cidr, PodCIDRs = [cidr] }
    };

    [Theory]
    [InlineData("old-pod")]
    [InlineData("unhealthy")]
    [InlineData("not-running")]
    [InlineData("wrong-pending-hash")]
    [InlineData("inactive-pending")]
    [InlineData("failed-pending")]
    public async Task UnprovenProxyTrialNeverReachesConfirmation(string failure)
    {
        using var environment = ProxyFixture();
        environment.Handler.Operation!.Status.HttpProxyFingerprint = "intended-http-fingerprint";
        environment.Handler.Operation.Status.HttpProxyStagedPodUid = "old-core-pod";
        environment.Handler.Pod!.Metadata.Uid = "replacement-core-pod";
        environment.Native.State = new(false, true, true, true, false, "intended-http-fingerprint", "intended-http-fingerprint");
        switch (failure)
        {
            case "old-pod": environment.Handler.Pod.Metadata.Uid = "old-core-pod"; break;
            case "unhealthy": environment.Handler.Pod.Status.Conditions[0].Status = "False"; break;
            case "not-running": environment.Native.CoreState = "STARTING"; break;
            case "wrong-pending-hash": environment.Native.State = environment.Native.State with { PendingFingerprint = "unrelated-http-fingerprint" }; break;
            case "inactive-pending": environment.Native.State = environment.Native.State with { PendingActive = false }; break;
            case "failed-pending": environment.Native.State = environment.Native.State with { PendingError = true }; break;
        }
        await environment.Controller.ReconcileAsync(environment.Instance, default);
        Assert.Equal(0, environment.Native.ConfirmCalls);
        Assert.NotEqual("Succeeded", environment.Handler.Operation.Status.Phase);
        Assert.Empty(environment.Handler.ReplicaWrites);
    }

    private sealed class Fixture : IDisposable
    {
        public HomeAssistantInstance Instance { get; } = new() { Metadata = new() { Name = HomeAssistantInstance.ResourceName,
            NamespaceProperty = "test-install", Uid = "instance", Generation = 1, ResourceVersion = "1" },
            Spec = new() { Command = new() { Id = new string('a', 32), Action = "Start" } } };
        public ConflictApi Handler { get; }
        public CoreLifecycle Lifecycle { get; }
        public InstanceController Controller { get; }
        public ProxyApi Native { get; } = new();
        private readonly KubeOps.KubernetesClient.KubernetesClient client;
        private readonly GatewayClient gateway;
        public Fixture()
        {
            Handler = new(Instance);
            var config = new KubernetesClientConfiguration { Host = "http://unit.invalid" };
            client = new(config, new Kubernetes(config, Handler));
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Token"] = "fixture" }).Build();
            gateway = new(settings, Native);
            Lifecycle = new(client, new Installation("test-install"), gateway, settings);
            Controller = new(Lifecycle);
        }
        public HomeAssistantOperation Operation(string phase) => new() { Metadata = new() { Name = CoreLifecycle.CommandId(Instance), NamespaceProperty = "test-install", ResourceVersion = "2" },
            Spec = new() { Id = CoreLifecycle.CommandId(Instance), TargetUid = "instance", TargetGeneration = Instance.Metadata.Generation ?? 1, Action = "Start", DesiredState = "Running", CreatedAt = DateTimeOffset.UtcNow }, Status = new() { Phase = phase } };
        public void Dispose() { client.Dispose(); gateway.Dispose(); }
    }
    [Fact]
    public async Task OperationCreationConflictRequeuesAndResumesThePersistedIntent()
    {
        var instance = new HomeAssistantInstance { Metadata = new() { Name = HomeAssistantInstance.ResourceName,
            NamespaceProperty = "test-install", Uid = "instance", Generation = 1, ResourceVersion = "1" },
            Spec = new() { Command = new() { Id = new string('a', 32), Action = "Start" } } };
        var handler = new ConflictApi(instance);
        var configuration = new KubernetesClientConfiguration { Host = "http://unit.invalid" };
        using var api = new Kubernetes(configuration, handler);
        using var client = new KubeOps.KubernetesClient.KubernetesClient(configuration, api);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Token"] = "fixture" }).Build();
        using var gateway = new GatewayClient(settings);
        var controller = new InstanceController(new CoreLifecycle(client, new Installation("test-install"), gateway, settings));
        var conflicted = await controller.ReconcileAsync(instance, CancellationToken.None);
        Assert.True(conflicted.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(2), conflicted.RequeueAfter);
        Assert.Equal("Accepted", handler.Operation!.Status.Phase);
        var retried = await controller.ReconcileAsync(instance, CancellationToken.None);
        Assert.True(retried.IsSuccess);
        Assert.Equal("Starting", handler.Operation.Status.Phase);
        Assert.Equal(1, handler.Creations);
        Assert.Equal(instance.Spec.Command.Id, handler.Operation.Spec.Id);
    }

    private sealed class ProxyApi : HttpMessageHandler
    {
        public string CoreState { get; set; } = "RUNNING";
        public HttpProxyState State { get; set; } = new(false, false, false, false, false, "intended-http-fingerprint", null);
        public int StageCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public string? ConfirmedFingerprint { get; private set; }
        public bool LoseNextStageResponse { get; set; }
        public string[] ExpectedTrustedProxies { get; set; } = ["10.244.69.17/32"];
        public string[]? LastTrustedProxies { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            object response;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/core/config") response = new { state = CoreState };
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                LastTrustedProxies = payload.RootElement.GetProperty("trusted_proxies").EnumerateArray().Select(value => value.GetString()!).ToArray();
                Assert.Equal(ExpectedTrustedProxies, LastTrustedProxies);
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/core/http/proxy-status": break;
                    case "/core/http/proxy-stage":
                        StageCalls++;
                        State = State with { PendingExists = true, PendingMatches = true, PendingFingerprint = State.DesiredFingerprint };
                        if (LoseNextStageResponse)
                        {
                            LoseNextStageResponse = false;
                            throw new HttpRequestException("Simulated lost response after native configuration committed.");
                        }
                        break;
                    case "/core/http/proxy-confirm":
                        ConfirmCalls++;
                        ConfirmedFingerprint = payload.RootElement.GetProperty("expected_fingerprint").GetString();
                        Assert.True(State.PendingActive && State.PendingMatches && !State.PendingError);
                        Assert.Equal(State.PendingFingerprint, ConfirmedFingerprint);
                        State = State with { StableMatches = true, PendingExists = false, PendingMatches = false, PendingActive = false, PendingFingerprint = null };
                        break;
                    default: throw new InvalidOperationException("Unexpected gateway request: " + request.RequestUri.AbsolutePath);
                }
                response = State;
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(response,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), Encoding.UTF8, "application/json") };
        }
    }

    // Return real Kubernetes wire responses through the official HTTP client;
    // no network request or ambient credentials can reach unit.invalid.
    private sealed class ConflictApi(HomeAssistantInstance instance) : DelegatingHandler
    {
        public static string TemplateYaml => KubernetesJson.Serialize(new V1StatefulSet { Metadata = new() { Name = "core" }, Spec = new()
            { Template = new() { Metadata = new(), Spec = new() { Containers = [new V1Container { Name = "core", Image = "fixture" }] } } } });
        public HomeAssistantOperation? Operation { get; set; }
        public V1Pod? Pod { get; set; }
        public V1StatefulSet? Workload { get; set; }
        public HomeAssistantInstance? PublishedInstance { get; private set; }
        public int Creations { get; private set; }
        public int MissingTemplateReads { get; set; }
        public int InstanceReadFailures { get; set; }
        public int IntentWrites { get; private set; }
        public List<int> ReplicaWrites { get; } = [];
        public List<HomeAssistantOperation> HistoricalOperations { get; } = [];
        public bool AllowIntentWrites { get; set; }
        public V1Node[] Nodes { get; set; } = [PodNetworkNode("worker-a", "10.244.1.0/24"), PodNetworkNode("worker-b", "10.244.2.0/24")];
        public int NodeReads { get; private set; }
        public bool NodeReadForbidden { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var response = await Respond(request, cancellation);
            response.RequestMessage = request;
            return response;
        }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/nodes")
            {
                NodeReads++;
                if (NodeReadForbidden) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":403}", Encoding.UTF8, "application/json") };
                return Json(new V1NodeList { Metadata = new(), Items = Nodes.ToList() });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantinstances/home-assistant") && InstanceReadFailures-- > 0)
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":403}", Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantinstances/home-assistant")) return Json(instance);
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantoperations"))
                return Json(new { apiVersion = "ha-operator.io/v1alpha1", kind = "HomeAssistantOperationList", metadata = new { }, items = HistoricalOperations.Concat(Operation is null ? [] : new[] { Operation }).ToArray() });
            if (request.Method == HttpMethod.Get && path.Contains("/homeassistantoperations/"))
            {
                var requested = path.Split('/')[^1];
                var found = Operation?.Metadata.Name == requested ? Operation : HistoricalOperations.FirstOrDefault(operation => operation.Metadata.Name == requested);
                return found is null ? NotFound() : Json(found);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/homeassistantoperations"))
            {
                Creations++;
                if (Operation is not null) HistoricalOperations.Add(Operation);
                Operation = KubernetesJson.Deserialize<HomeAssistantOperation>(await request.Content!.ReadAsStringAsync(cancellation));
                Operation.Metadata.ResourceVersion = "2";
                return new(HttpStatusCode.Conflict) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Failure\",\"reason\":\"AlreadyExists\",\"code\":409}", Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/homeassistantinstances/home-assistant/status"))
            {
                PublishedInstance = KubernetesJson.Deserialize<HomeAssistantInstance>(await request.Content!.ReadAsStringAsync(cancellation));
                instance.Status = PublishedInstance.Status;
                return Json(PublishedInstance);
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/homeassistantinstances/home-assistant"))
            {
                IntentWrites++;
                if (!AllowIntentWrites) throw new InvalidOperationException("This fixture does not authorize new durable intent writes.");
                var accepted = KubernetesJson.Deserialize<HomeAssistantInstance>(await request.Content!.ReadAsStringAsync(cancellation));
                instance.Spec = accepted.Spec;
                instance.Metadata.Generation = (instance.Metadata.Generation ?? 1) + 1;
                instance.Metadata.ResourceVersion = "6";
                return Json(instance);
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/status"))
            {
                Operation = KubernetesJson.Deserialize<HomeAssistantOperation>(await request.Content!.ReadAsStringAsync(cancellation));
                Operation.Metadata.ResourceVersion = "3";
                return Json(Operation);
            }
            if (path.EndsWith("/configmaps/core-workload") && MissingTemplateReads-- > 0) return NotFound();
            if (path.EndsWith("/configmaps/core-workload")) return Json(new V1ConfigMap { Metadata = new(), Data = new Dictionary<string, string>()
                { ["core.yaml"] = TemplateYaml } });
            if (path.EndsWith("/pods/core-0")) return Pod is null ? NotFound() : Json(Pod);
            if (request.Method == HttpMethod.Put && path.EndsWith("/statefulsets/core"))
            {
                Workload = KubernetesJson.Deserialize<V1StatefulSet>(await request.Content!.ReadAsStringAsync(cancellation));
                Workload.Metadata.ResourceVersion = "5";
                ReplicaWrites.Add(Workload.Spec.Replicas ?? 1);
                return Json(Workload);
            }
            if (path.EndsWith("/statefulsets/core")) return Workload is null ? NotFound() : Json(Workload);
            throw new InvalidOperationException("Unexpected Kubernetes fixture request: " + request.Method + " " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(KubernetesJson.Serialize(value), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":404}", Encoding.UTF8, "application/json") };
    }
}
