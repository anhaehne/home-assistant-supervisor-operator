using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace SupervisorOperator;

[ApiController]
public sealed class OptionsController(InstanceOptionsStore store) : SupervisorControllerBase
{
    [HttpPost("/supervisor/options")]
    public async Task<IActionResult> SupervisorOptions()
    {
        using var body = await ReadObject(Request);
        foreach (var property in body.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "timezone" when property.Value.ValueKind == JsonValueKind.String:
                    try { _ = TimeZoneInfo.FindSystemTimeZoneById(property.Value.GetString()!); }
                    catch (TimeZoneNotFoundException) { throw new ApiValidationException("Invalid timezone"); }
                    break;
                case "country" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length == 2:
                case "diagnostics" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    break;
                default: throw new ApiValidationException("Unsupported or invalid Supervisor option: " + property.Name);
            }
        }
        await store.Update(current => current with
        {
            Timezone = body.RootElement.TryGetProperty("timezone", out var timezone) ? timezone.GetString()! : current.Timezone,
            Country = body.RootElement.TryGetProperty("country", out var country) ? country.GetString() : current.Country,
            Diagnostics = body.RootElement.TryGetProperty("diagnostics", out var diagnostics) ? diagnostics.GetBoolean() : current.Diagnostics
        }, HttpContext.RequestAborted);
        return ApiOk(new { });
    }

    [HttpPost("/core/options")]
    [HttpPost("/homeassistant/options")]
    public async Task<IActionResult> CoreOptions()
    {
        using var body = await ReadObject(Request);
        foreach (var property in body.RootElement.EnumerateObject())
            if (property.Name switch
            {
                "port" => property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var port) || port is < 1 or > 65535,
                "ssl" => property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False),
                "refresh_token" => property.Value.ValueKind != JsonValueKind.Null,
                _ => true
            }) throw new ApiValidationException("Unsupported or invalid Core option: " + property.Name);
        await store.Update(current => current with
        {
            Port = body.RootElement.TryGetProperty("port", out var port) ? port.GetInt32() : current.Port,
            Ssl = body.RootElement.TryGetProperty("ssl", out var ssl) ? ssl.GetBoolean() : current.Ssl
        }, HttpContext.RequestAborted);
        return ApiOk(new { });
    }

    [HttpPost("/supervisor/update")]
    public async Task<IActionResult> Update()
    {
        using var body = await ReadObject(Request);
        foreach (var property in body.RootElement.EnumerateObject())
            if (property.Name != "version" || property.Value.ValueKind != JsonValueKind.String)
                throw new ApiValidationException("Invalid update options");
        return ApiError("No supervisor update available - operator development build");
    }
}
