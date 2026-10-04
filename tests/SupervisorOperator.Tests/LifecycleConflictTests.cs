using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using SupervisorOperator.Lifecycle;

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
        { Uid = "current-workload", Annotations = new Dictionary<string, string> { [CoreLifecycle.Owner] = instance.Metadata.Uid } },
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

    private sealed class Fixture : IDisposable
    {
        public HomeAssistantInstance Instance { get; } = new() { Metadata = new() { Name = HomeAssistantInstance.ResourceName,
            NamespaceProperty = "test-install", Uid = "instance", Generation = 1, ResourceVersion = "1" },
            Spec = new() { Command = new() { Id = new string('a', 32), Action = "Start" } } };
        public ConflictApi Handler { get; }
        public CoreLifecycle Lifecycle { get; }
        public InstanceController Controller { get; }
        private readonly KubeOps.KubernetesClient.KubernetesClient client;
        private readonly GatewayClient gateway;
        public Fixture()
        {
            Handler = new(Instance);
            var config = new KubernetesClientConfiguration { Host = "http://unit.invalid" };
            client = new(config, new Kubernetes(config, Handler));
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Token"] = "fixture" }).Build();
            gateway = new(settings);
            Lifecycle = new(client, new Installation("test-install"), gateway, settings);
            Controller = new(Lifecycle);
        }
        public HomeAssistantOperation Operation(string phase) => new() { Metadata = new() { Name = CoreLifecycle.CommandId(Instance), NamespaceProperty = "test-install", ResourceVersion = "2" },
            Spec = new() { Id = CoreLifecycle.CommandId(Instance), TargetUid = "instance", TargetGeneration = 1, Action = "Start", DesiredState = "Running", CreatedAt = DateTimeOffset.UtcNow }, Status = new() { Phase = phase } };
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var response = await Respond(request, cancellation);
            response.RequestMessage = request;
            return response;
        }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantinstances/home-assistant") && InstanceReadFailures-- > 0)
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":403}", Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantinstances/home-assistant")) return Json(instance);
            if (request.Method == HttpMethod.Get && path.EndsWith("/homeassistantoperations"))
                return Json(new { apiVersion = "ha-operator.io/v1alpha1", kind = "HomeAssistantOperationList", metadata = new { }, items = Operation is null ? Array.Empty<HomeAssistantOperation>() : new[] { Operation } });
            if (request.Method == HttpMethod.Get && path.Contains("/homeassistantoperations/")) return Operation is null ? NotFound() : Json(Operation);
            if (request.Method == HttpMethod.Post && path.EndsWith("/homeassistantoperations"))
            {
                Creations++;
                Operation = KubernetesJson.Deserialize<HomeAssistantOperation>(await request.Content!.ReadAsStringAsync(cancellation));
                Operation.Metadata.ResourceVersion = "2";
                return new(HttpStatusCode.Conflict) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Failure\",\"reason\":\"AlreadyExists\",\"code\":409}", Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/homeassistantinstances/home-assistant/status"))
            {
                PublishedInstance = KubernetesJson.Deserialize<HomeAssistantInstance>(await request.Content!.ReadAsStringAsync(cancellation));
                return Json(PublishedInstance);
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
            if (path.EndsWith("/statefulsets/core")) return Workload is null ? NotFound() : Json(Workload);
            throw new InvalidOperationException("Unexpected Kubernetes fixture request: " + request.Method + " " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(KubernetesJson.Serialize(value), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":404}", Encoding.UTF8, "application/json") };
    }
}
