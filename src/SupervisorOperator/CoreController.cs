using Microsoft.AspNetCore.Mvc;

namespace SupervisorOperator;

[ApiController]
[Route("core")]
[Route("homeassistant")]
public sealed class CoreController(P0ReadModels models, CoreMetrics metrics, GatewayClient gateway) : SupervisorControllerBase
{
    [HttpGet("info")]
    public async Task<IActionResult> Info(CancellationToken cancellation) => ApiOk(await models.Core(cancellation));

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellation) => ApiOk(await metrics.Read(cancellation));

    // Exact privileged reads only; callers cannot choose upstream targets.
    [HttpGet("api/")]
    public Task<IActionResult> Status(CancellationToken cancellation) => Proxy("/core/status", cancellation);

    [HttpGet("api/config")]
    public Task<IActionResult> Configuration(CancellationToken cancellation) => Proxy("/core/config", cancellation);

    private Task<IActionResult> Proxy(string route, CancellationToken cancellation) => Request.QueryString.HasValue
        ? Task.FromResult<IActionResult>(ApiError("Proxy query parameters are unavailable"))
        : gateway.Proxy(route, cancellation);
}
