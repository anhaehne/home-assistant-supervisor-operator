using System.Text.Json.Serialization;
using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace SupervisorOperator.Foundation;

// Disposable framework acceptance resource; not the production instance or operation API.
[KubernetesEntity(Group = "spikes.ha-operator.io", ApiVersion = "v1alpha1", Kind = "FrameworkProbe", PluralName = "frameworkprobes")]
public sealed class FrameworkProbe : CustomKubernetesEntity<ProbeSpec, ProbeStatus>
{
    public const string Name = "framework";
    public const string StateName = "framework-probe-state";
    // KubeOps 13.3.1 auto-attachment derives this from the group and class name.
    // The keyed DI registration must match the attached name for deletion dispatch.
    public const string Finalizer = "spikes.ha-operator.io/probefinalizer";
}

public sealed class ProbeSpec
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
    [JsonPropertyName("hold")]
    public bool Hold { get; set; }
}

public sealed class ProbeStatus
{
    [JsonPropertyName("observedGeneration")]
    public long ObservedGeneration { get; set; }
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";
}
