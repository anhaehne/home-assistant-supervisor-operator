using Microsoft.AspNetCore.Mvc;

namespace SupervisorOperator.Lifecycle;

[ApiController]
public sealed class JobsController(IServiceProvider services) : SupervisorControllerBase
{
    [HttpGet("/jobs/info")]
    public async Task<IActionResult> Jobs(CancellationToken cancellation)
    {
        var lifecycle = services.GetService<CoreLifecycle>();
        var jobs = lifecycle is null ? [] : (await lifecycle.Operations(cancellation)).Select(Project).ToArray();
        return ApiOk(new { ignore_conditions = Array.Empty<string>(), jobs });
    }

    [HttpGet("/jobs/{uuid}")]
    public async Task<IActionResult> Job(string uuid, CancellationToken cancellation)
    {
        var lifecycle = services.GetService<CoreLifecycle>();
        var operation = lifecycle is null ? null : (await lifecycle.Operations(cancellation)).FirstOrDefault(op => op.Spec.Id == uuid);
        return operation is null ? ApiError("Job does not exist", 404) : ApiOk(Project(operation));
    }

    public static object Project(HomeAssistantOperation operation) => new
    {
        name = "home_assistant_core_" + operation.Spec.Action.ToLowerInvariant(), reference = "home-assistant",
        uuid = operation.Spec.Id, stage = operation.Status.Phase, done = operation.Status.Terminal,
        progress = operation.Status.Terminal ? 100 : operation.Status.Phase switch { "Stopping" => 20, "Starting" => 50, "WaitingForHealth" => 70, _ => 0 },
        created = operation.Spec.CreatedAt, extra = (object?)null, child_jobs = Array.Empty<object>(),
        errors = operation.Status.Error is null ? Array.Empty<object>() : [new { type = "CoreLifecycleError", message = operation.Status.Error,
            stage = operation.Status.Phase, error_key = (string?)null, extra_fields = (object?)null }]
    };
}
