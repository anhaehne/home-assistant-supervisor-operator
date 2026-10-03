using Microsoft.AspNetCore.Mvc;
using SharedRuntime;

namespace CoreGateway;

[ApiController]
public sealed class CoreController(CoreSocketClient client, ILogger<CoreController> logger) : ControllerBase
{
    [HttpGet("/health/live")]
    public IActionResult Live() => Ok();

    [HttpGet("/core/metadata")]
    public IActionResult Metadata() => new JsonResult(RuntimeMetrics.Network());

    // The private socket grants Supervisor identity. Only these fixed reads
    // exist; no route accepts a caller-selected upstream URL or command.
    [HttpGet("/core/status")]
    public Task Status() => CopyResponse(client.ReadStatus);

    [HttpGet("/core/config")]
    public Task Configuration() => CopyResponse(client.ReadConfiguration);

    private async Task CopyResponse(Func<CancellationToken, Task<HttpResponseMessage>> read)
    {
        try
        {
            using var response = await read(HttpContext.RequestAborted);
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Core Unix socket read failed for {Route}", Request.Path);
            Response.StatusCode = 503;
        }
    }
}
