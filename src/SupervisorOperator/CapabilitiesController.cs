using Microsoft.AspNetCore.Mvc;
using Supervisor.Contracts;

namespace SupervisorOperator;

[ApiController]
public sealed class CapabilitiesController : SupervisorControllerBase
{
    [HttpGet("/store")]
    public IActionResult Store() => ApiOk(new StoreInfo([], []));

    [HttpGet("/store/addons")]
    [HttpGet("/addons")]
    public IActionResult Addons() => ApiOk(new AddonsInfo([]));

    [HttpGet("/store/repositories")]
    public IActionResult Repositories() => ApiOk(new { repositories = Array.Empty<object>() });

    [HttpGet("/discovery")]
    public IActionResult Discovery() => ApiOk(new { discovery = Array.Empty<object>() });

    [HttpGet("/services")]
    public IActionResult Services() => ApiOk(new { services = Array.Empty<object>() });

    [HttpGet("/services/{service}")]
    public IActionResult Service() => ApiError("No service is registered", 404);

    [HttpGet("/backups")]
    public IActionResult Backups() => ApiOk(new { backups = Array.Empty<object>() });

    [HttpGet("/backups/info")]
    public IActionResult BackupInfo() => ApiOk(new { backups = Array.Empty<object>(), days_until_stale = 30 });

    [HttpGet("/mounts")]
    public IActionResult Mounts() => ApiOk(new MountsInfo(null, []));

    [HttpGet("/ingress/panels")]
    public IActionResult Panels() => ApiOk(new IngressInfo(new()));

    [HttpGet("/resolution/info")]
    public IActionResult Resolution() => ApiOk(new ResolutionInfo([P0ReadModels.Limitation], [], [], [], []));

    [HttpGet("/available_updates")]
    [HttpGet("/supervisor/available_updates")]
    public IActionResult Updates() => ApiOk(new { available_updates = Array.Empty<object>() });

    [Route("/{**path}", Order = int.MaxValue)]
    public IActionResult Unavailable() => ApiError(Request.Path.StartsWithSegments("/v2")
        ? "Supervisor v2 API is unavailable"
        : "This operation is unavailable in the Kubernetes operator P0 preview");
}
