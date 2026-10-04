using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace SupervisorOperator.Lifecycle;

[KubernetesEntity(Group = "ha-operator.io", ApiVersion = "v1alpha1", Kind = "HomeAssistantInstance", PluralName = "homeassistantinstances")]
public sealed class HomeAssistantInstance : CustomKubernetesEntity<InstanceSpec, InstanceStatus>
{
    public HomeAssistantInstance() { ApiVersion = "ha-operator.io/v1alpha1"; Kind = "HomeAssistantInstance"; }
    public const string ResourceName = "home-assistant";
    public const string Finalizer = "ha-operator.io/instancefinalizer";
}

public sealed class InstanceSpec
{
    public string DesiredState { get; set; } = "Running";
    public string Ownership { get; set; } = "Ui";
    public string SelectedNode { get; set; } = "";
    public InstanceOptions Options { get; set; } = new(Country: "US");
    public CoreCommand? Command { get; set; }
}

public sealed class CoreCommand
{
    public string Id { get; set; } = "";
    public string Action { get; set; } = "Reconcile";
    public string? RequestKey { get; set; }
}

public sealed class InstanceStatus
{
    public long ObservedGeneration { get; set; }
    public string? OperationId { get; set; }
    public string State { get; set; } = "Unknown";
    public string? PodUid { get; set; }
    public List<V1Condition> Conditions { get; set; } = [];
}

[KubernetesEntity(Group = "ha-operator.io", ApiVersion = "v1alpha1", Kind = "HomeAssistantOperation", PluralName = "homeassistantoperations")]
public sealed class HomeAssistantOperation : CustomKubernetesEntity<OperationSpec, OperationStatus>
{
    public HomeAssistantOperation() { ApiVersion = "ha-operator.io/v1alpha1"; Kind = "HomeAssistantOperation"; }
}

public sealed class OperationSpec
{
    public string Id { get; set; } = "";
    public string TargetUid { get; set; } = "";
    public long TargetGeneration { get; set; }
    public string Action { get; set; } = "";
    public string DesiredState { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OperationStatus
{
    public string Phase { get; set; } = "Accepted";
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Terminal => Phase is "Succeeded" or "Failed";
}

public sealed record Installation(string Namespace);
