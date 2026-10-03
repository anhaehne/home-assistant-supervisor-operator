using Microsoft.AspNetCore.Mvc;
using Supervisor.Contracts;

namespace SupervisorOperator;

[ApiController]
public sealed class InfoController(P0ReadModels models) : SupervisorControllerBase
{
    [HttpGet("/health/live")]
    public IActionResult Live() => Ok();

    [HttpGet("/supervisor/ping")]
    public IActionResult Ping() => ApiOk(new { });

    [HttpGet("/info")]
    public IActionResult Info() => ApiOk(models.Root());

    [HttpGet("/supervisor/info")]
    public IActionResult Supervisor() => ApiOk(models.Supervisor());

    [HttpGet("/supervisor/stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellation) => ApiOk(await models.SupervisorStats(cancellation));

    [HttpGet("/host/info")]
    public IActionResult Host() => ApiOk(models.Host());

    [HttpGet("/os/info")]
    public IActionResult OperatingSystem() => ApiOk(new OsInfo(BootSlots: new()));

    [HttpGet("/network/info")]
    public async Task<IActionResult> Network(CancellationToken cancellation) => ApiOk(await models.Network(cancellation));

    [HttpGet("/operator/info")]
    public IActionResult Operator() => ApiOk(new
    {
        version = P0ReadModels.BuildVersion, api_compatibility = "2026.09.3", core_baseline = "2026.9.4",
        installation = "kubernetes", limitations = new[] { P0ReadModels.Limitation },
        statistics_scope = "container cgroup CPU, memory and block IO; Pod network counters",
        host_disk_scope = "compatibility-state PVC filesystem"
    });
}
