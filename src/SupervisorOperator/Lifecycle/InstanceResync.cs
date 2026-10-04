using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Queue;

namespace SupervisorOperator.Lifecycle;

// KubeOps can exhaust retries before invoking the controller (its own GET).
// Poll only our fixed singleton so dependency recovery needs no watch event.
public sealed class InstanceResync(IServiceScopeFactory scopes, ILogger<InstanceResync> logger) : BackgroundService
{
    public static async Task<bool> Reschedule(CoreLifecycle lifecycle, EntityQueue<HomeAssistantInstance> queue, CancellationToken cancellation)
    {
        var instance = await lifecycle.Instance(cancellation);
        return instance is not null && await queue(instance, ReconciliationType.Modified, ReconciliationTriggerSource.Operator,
            TimeSpan.Zero, 0, cancellation);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await Reschedule(scope.ServiceProvider.GetRequiredService<CoreLifecycle>(),
                    scope.ServiceProvider.GetRequiredService<EntityQueue<HomeAssistantInstance>>(), stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Instance resync will retry after {FailureType}", exception.GetType().Name);
            }
        }
    }
}
