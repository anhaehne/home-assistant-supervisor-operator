using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using Supervisor.Contracts;

namespace CoreGateway;

[ApiController]
public sealed class HttpProxyController(CoreHttpProxyClient client, ILogger<HttpProxyController> logger) : ControllerBase
{
    [HttpPost("/core/http/proxy-status")]
    public Task<IActionResult> Status(HttpProxySettings settings) => Run(() => client.Status(settings, HttpContext.RequestAborted));
    [HttpPost("/core/http/proxy-stage")]
    public Task<IActionResult> Stage(HttpProxySettings settings) => Run(() => client.Stage(settings, HttpContext.RequestAborted));
    [HttpPost("/core/http/proxy-confirm")]
    public Task<IActionResult> Confirm(HttpProxyConfirmation confirmation) => Run(() => client.Confirm(confirmation, HttpContext.RequestAborted));

    private async Task<IActionResult> Run(Func<Task<HttpProxyState>> action)
    {
        try { return Ok(await action()); }
        catch (ArgumentException) { return BadRequest(new { message = "Invalid trusted proxy addresses or networks." }); }
        catch (HttpProxyConflictException exception) { return Conflict(new { message = exception.Message }); }
        catch (Exception exception) when (exception is HttpRequestException or WebSocketException or IOException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Native HTTP configuration operation failed for {Route}", Request.Path);
            return StatusCode(503, new { message = "Native HTTP configuration is unavailable." });
        }
    }
}
