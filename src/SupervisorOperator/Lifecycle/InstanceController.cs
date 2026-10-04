using System.Net;
using k8s.Autorest;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;

namespace SupervisorOperator.Lifecycle;

public sealed class InstanceController(CoreLifecycle lifecycle, ILogger<InstanceController>? logger = null) : IEntityController<HomeAssistantInstance>
{
    public async Task<ReconciliationResult<HomeAssistantInstance>> ReconcileAsync(HomeAssistantInstance entity, CancellationToken cancellationToken)
    {
        try
        {
            var current = await lifecycle.Instance(cancellationToken);
            if (current is null || current.Metadata.Uid != entity.Metadata.Uid || current.Metadata.DeletionTimestamp is not null)
                return ReconciliationResult<HomeAssistantInstance>.Success(entity);
            await lifecycle.Step(current, cancellationToken);
            return ReconciliationResult<HomeAssistantInstance>.Success(current, TimeSpan.FromSeconds(2));
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Returning an explicit retry avoids the framework's finite error
            // retry budget. Dependencies can recover without an instance event.
            logger?.LogWarning("Core reconciliation will retry after {FailureType}", exception.GetType().Name);
            await lifecycle.ReportFailure(entity.Metadata.Uid, cancellationToken);
            return ReconciliationResult<HomeAssistantInstance>.Failure(entity, "Core reconciliation dependency failed; retrying", requeueAfter: TimeSpan.FromSeconds(5));
        }
        return ReconciliationResult<HomeAssistantInstance>.Success(entity, TimeSpan.FromSeconds(2));
    }

    public Task<ReconciliationResult<HomeAssistantInstance>> DeletedAsync(HomeAssistantInstance entity, CancellationToken cancellationToken) =>
        Task.FromResult(ReconciliationResult<HomeAssistantInstance>.Success(entity));
}

public sealed class InstanceFinalizer(CoreLifecycle lifecycle, ILogger<InstanceFinalizer>? logger = null) : IEntityFinalizer<HomeAssistantInstance>
{
    public async Task<ReconciliationResult<HomeAssistantInstance>> FinalizeAsync(HomeAssistantInstance entity, CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycle.Finalize(entity, cancellationToken)
                ? ReconciliationResult<HomeAssistantInstance>.Success(entity)
                : ReconciliationResult<HomeAssistantInstance>.Failure(entity, "Waiting for graceful Core termination", requeueAfter: TimeSpan.FromSeconds(2));
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return ReconciliationResult<HomeAssistantInstance>.Failure(entity, "Core finalization conflicted", requeueAfter: TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Core finalization will retry after {FailureType}", exception.GetType().Name);
            return ReconciliationResult<HomeAssistantInstance>.Failure(entity, "Core finalization dependency failed; data retained", requeueAfter: TimeSpan.FromSeconds(5));
        }
    }
}
