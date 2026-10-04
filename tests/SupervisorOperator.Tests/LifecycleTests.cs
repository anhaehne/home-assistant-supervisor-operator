using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupervisorOperator.Lifecycle;

namespace SupervisorOperator.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void NewDurableOperationsCarryTheirKubernetesWireIdentity()
    {
        using var document = System.Text.Json.JsonDocument.Parse(k8s.KubernetesJson.Serialize(new HomeAssistantOperation()));
        Assert.Equal("HomeAssistantOperation", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("ha-operator.io/v1alpha1", document.RootElement.GetProperty("apiVersion").GetString());
    }
    private static HomeAssistantInstance Instance() => new() { Metadata = new() { Uid = "instance-one", Generation = 4 } };

    [Fact]
    public void CurrentCommandWaitsForInstancePublicationButHistoricalRetryDoesNot()
    {
        var instance = Instance();
        instance.Spec.Command = new CoreCommand { Id = "current", Action = "Stop" };
        var operation = new HomeAssistantOperation { Spec = new OperationSpec { Id = "current", TargetUid = "instance-one" },
            Status = new OperationStatus { Phase = "Succeeded" } };
        Assert.False(CoreLifecycle.CompletionPublished(instance, operation));
        instance.Status.OperationId = "current";
        instance.Status.ObservedGeneration = 3;
        Assert.False(CoreLifecycle.CompletionPublished(instance, operation));
        instance.Status.ObservedGeneration = 4;
        Assert.False(CoreLifecycle.CompletionPublished(instance, operation));
        instance.Status.Conditions = [new V1Condition { Type = "Ready", Status = "True", Reason = "Succeeded" }];
        Assert.True(CoreLifecycle.CompletionPublished(instance, operation));
        instance.Spec.Command.Id = "next";
        Assert.True(CoreLifecycle.CompletionPublished(instance, operation));
        operation.Spec.TargetUid = "deleted-instance";
        Assert.False(CoreLifecycle.CompletionPublished(instance, operation));
    }

    [Fact]
    public void StaleControllerCannotWriteAfterNewerWorkloadFence()
    {
        var workload = new V1StatefulSet { Metadata = new() { Annotations = new Dictionary<string, string>
            { [CoreLifecycle.Owner] = "instance-one", [CoreLifecycle.Fence] = "5" } } };
        Assert.Throws<InvalidOperationException>(() => CoreLifecycle.ValidateFence(workload, Instance()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other-instance")]
    public void WorkloadFromAnotherInstanceIsNeverAdopted(string? owner)
    {
        var workload = new V1StatefulSet { Metadata = new() { Annotations = new Dictionary<string, string>() } };
        if (owner is not null) workload.Metadata.Annotations[CoreLifecycle.Owner] = owner;
        Assert.Throws<InvalidOperationException>(() => CoreLifecycle.ValidateFence(workload, Instance()));
    }

    [Fact]
    public void SameOrOlderWorkloadFenceCanBeReconciled()
    {
        var workload = new V1StatefulSet { Metadata = new() { Annotations = new Dictionary<string, string>
            { [CoreLifecycle.Owner] = "instance-one", [CoreLifecycle.Fence] = "4" } } };
        CoreLifecycle.ValidateFence(workload, Instance());
    }

    [Fact]
    public void TerminatingPodIsNotApplicationReady()
    {
        var pod = new V1Pod { Metadata = new() { DeletionTimestamp = DateTime.UtcNow },
            Status = new() { Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }] } };
        Assert.False(CoreLifecycle.Ready(pod));
        Assert.False(CoreLifecycle.Ready(null));
    }

    [Fact]
    public void AcceptedCommandIdentitySurvivesOptionGenerationChanges()
    {
        var instance = Instance();
        instance.Spec.Command = new CoreCommand { Id = new string('a', 32), Action = "Restart" };
        var id = CoreLifecycle.CommandId(instance);
        instance.Metadata.Generation++;
        Assert.Equal(id, CoreLifecycle.CommandId(instance));
        instance.Spec.Ownership = "GitOps";
        Assert.NotEqual(id, CoreLifecycle.CommandId(instance));
    }

    [Fact]
    public void FinalizerRegistrationMatchesAutomaticAttachment()
    {
        Assert.Equal(HomeAssistantInstance.Finalizer, new InstanceFinalizer(null!).GetIdentifierName(Instance()));
    }

    [Fact]
    public void LocalApiHasNoAmbientLifecycleRegistration()
    {
        var services = new ServiceCollection();
        services.AddLifecycleOperator(new ConfigurationBuilder().Build());
        Assert.Empty(services);
        Assert.Throws<InvalidOperationException>(() => services.AddLifecycleOperator(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Operator:Enabled"] = "true" }).Build()));
        Assert.Empty(services);
    }
}
