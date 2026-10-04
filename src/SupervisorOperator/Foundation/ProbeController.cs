using System.Net;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace SupervisorOperator.Foundation;

public sealed class ProbeController(IKubernetesClient client, ProbeInstallation installation) : IEntityController<FrameworkProbe>
{
    public async Task<ReconciliationResult<FrameworkProbe>> ReconcileAsync(FrameworkProbe entity, CancellationToken cancellationToken)
    {
        installation.Validate(entity);
        // A queued event can be stale. Re-read durable intent for every attempt.
        var current = await client.GetAsync<FrameworkProbe>(FrameworkProbe.Name, installation.Namespace, cancellationToken);
        if (current is null || current.Metadata.Uid != entity.Metadata.Uid || current.Metadata.DeletionTimestamp is not null)
            return ReconciliationResult<FrameworkProbe>.Success(entity);
        entity = current;
        var generation = entity.Metadata.Generation ?? 0;
        try
        {
            if (entity.Status.ObservedGeneration != generation || string.IsNullOrEmpty(entity.Status.Phase))
            {
                entity.Status = new ProbeStatus { ObservedGeneration = generation, Phase = "Accepted" };
                entity = await client.UpdateStatusAsync(entity, cancellationToken);
            }
            if (entity.Spec.Hold)
                return ReconciliationResult<FrameworkProbe>.Success(entity, TimeSpan.FromSeconds(2));

            var state = await client.GetAsync<V1ConfigMap>(FrameworkProbe.StateName, installation.Namespace, cancellationToken)
                ?? throw new InvalidOperationException("The framework probe state ConfigMap is missing.");
            state.Data ??= new Dictionary<string, string>();
            if (state.Read("ownerUid") is { Length: > 0 } owner && owner != entity.Metadata.Uid)
                return ReconciliationResult<FrameworkProbe>.Failure(entity, "Probe state belongs to another resource", requeueAfter: TimeSpan.FromSeconds(2));
            if (state.Read("value") != entity.Spec.Value || state.Read("ownerUid") != entity.Metadata.Uid)
            {
                state.Data["ownerUid"] = entity.Metadata.Uid;
                state.Data["value"] = entity.Spec.Value;
                // Replace uses resourceVersion: stale writers fail rather than silently overwriting state.
                await client.UpdateAsync(state, cancellationToken);
            }
            if (entity.Status.Phase != "Completed")
            {
                entity.Status.Phase = "Completed";
                entity = await client.UpdateStatusAsync(entity, cancellationToken);
            }
            // Periodic reconciliation repairs ConfigMap drift without granting cluster-wide watches.
            return ReconciliationResult<FrameworkProbe>.Success(entity, TimeSpan.FromSeconds(2));
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return ReconciliationResult<FrameworkProbe>.Success(entity, TimeSpan.FromSeconds(1));
        }
    }

    public Task<ReconciliationResult<FrameworkProbe>> DeletedAsync(FrameworkProbe entity, CancellationToken cancellationToken) =>
        Task.FromResult(ReconciliationResult<FrameworkProbe>.Success(entity));
}

public sealed class ProbeFinalizer(IKubernetesClient client, ProbeInstallation installation) : IEntityFinalizer<FrameworkProbe>
{
    public async Task<ReconciliationResult<FrameworkProbe>> FinalizeAsync(FrameworkProbe entity, CancellationToken cancellationToken)
    {
        installation.Validate(entity);
        try
        {
            var state = await client.GetAsync<V1ConfigMap>(FrameworkProbe.StateName, installation.Namespace, cancellationToken);
            if (state?.Data is null) return ReconciliationResult<FrameworkProbe>.Success(entity);
            // An explicit test barrier demonstrates that failed cleanup retains the finalizer.
            if (state.Read("cleanupBlocked") == "true")
                return ReconciliationResult<FrameworkProbe>.Failure(entity, "Probe cleanup is blocked", requeueAfter: TimeSpan.FromSeconds(2));
            if (state.Read("ownerUid") == entity.Metadata.Uid)
            {
                state.Data.Remove("ownerUid");
                state.Data.Remove("value");
                await client.UpdateAsync(state, cancellationToken);
            }
            return ReconciliationResult<FrameworkProbe>.Success(entity);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return ReconciliationResult<FrameworkProbe>.Failure(entity, "Probe cleanup conflicted", requeueAfter: TimeSpan.FromSeconds(1));
        }
    }
}

internal static class ProbeState
{
    internal static string? Read(this V1ConfigMap state, string key) =>
        state.Data?.TryGetValue(key, out var value) == true ? value : null;
}
