using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Supervisor.Contracts;

namespace SupervisorOperator;

public abstract class SupervisorControllerBase : ControllerBase
{
    protected static JsonResult ApiOk<T>(T data) => new(new ApiSuccess<T>(data));
    protected static JsonResult ApiError(string message, int statusCode = 400) =>
        new(new ApiError(message)) { StatusCode = statusCode };

    protected static async Task<JsonDocument> ReadObject(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new ApiValidationException("Expected an object");
    }
}
