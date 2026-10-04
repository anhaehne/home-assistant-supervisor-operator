using Microsoft.AspNetCore.Mvc;
using k8s;
using SupervisorOperator.Lifecycle;

namespace SupervisorOperator;

[ApiController]
[Route("core")]
[Route("homeassistant")]
public sealed class CoreController(P0ReadModels models, CoreMetrics metrics, GatewayClient gateway, IServiceProvider services) : SupervisorControllerBase
{
    [HttpGet("info")]
    public async Task<IActionResult> Info(CancellationToken cancellation) => ApiOk(await models.Core(cancellation));

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellation)
    {
        var lifecycle = services.GetService<CoreLifecycle>();
        if (lifecycle is not null && await lifecycle.Pod(cancellation) is null) return ApiError("Core is stopped");
        return ApiOk(await metrics.Read(cancellation));
    }

    [HttpPost("start")]
    public Task<IActionResult> Start() => Change("Start");
    [HttpPost("stop")]
    public Task<IActionResult> Stop() => Change("Stop");
    [HttpPost("restart")]
    public Task<IActionResult> Restart() => Change("Restart");

    private async Task<IActionResult> Change(string action)
    {
        var lifecycle = services.GetService<CoreLifecycle>();
        if (lifecycle is null) return ApiError("Core lifecycle requires the installed operator");
        using var body = await ReadObject(Request);
        foreach (var property in body.RootElement.EnumerateObject())
            if (property.Name is not ("force" or "safe_mode") || property.Value.ValueKind != System.Text.Json.JsonValueKind.False ||
                action == "Start" || action == "Stop" && property.Name == "safe_mode")
                throw new ApiValidationException("Unsupported lifecycle option: " + property.Name);
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var id = await lifecycle.Accept(action, key, HttpContext.RequestAborted);
        await lifecycle.Wait(id, HttpContext.RequestAborted);
        return ApiOk(new { });
    }

    [HttpGet("logs")]
    [HttpGet("logs/latest")]
    public async Task<IActionResult> Logs(CancellationToken cancellation)
    {
        var client = services.GetService<KubeOps.KubernetesClient.IKubernetesClient>();
        var installation = services.GetService<Installation>();
        if (client is null || installation is null) return ApiError("Core logs require the installed operator");
        var pod = await services.GetRequiredService<CoreLifecycle>().Pod(cancellation);
        if (pod is null) return ApiError("Core is stopped; retained boot logs are unavailable");
        var stream = await client.ApiClient.CoreV1.ReadNamespacedPodLogAsync("core-0", installation.Namespace, container: "core", tailLines: 1000, cancellationToken: cancellation);
        return File(stream, "text/plain");
    }

    // Exact privileged reads only; callers cannot choose upstream targets.
    [HttpGet("api/")]
    public Task<IActionResult> Status(CancellationToken cancellation) => Proxy("/core/status", cancellation);

    [HttpGet("api/config")]
    public Task<IActionResult> Configuration(CancellationToken cancellation) => Proxy("/core/config", cancellation);

    private Task<IActionResult> Proxy(string route, CancellationToken cancellation) => Request.QueryString.HasValue
        ? Task.FromResult<IActionResult>(ApiError("Proxy query parameters are unavailable"))
        : gateway.Proxy(route, cancellation);
}
