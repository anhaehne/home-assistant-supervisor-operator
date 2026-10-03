using System.Text.Json.Serialization;

namespace Supervisor.Contracts;

// v1 wire models are independent of Kubernetes resources and operator state.
public sealed record ApiSuccess<T>(
    [property: JsonPropertyName("data")] T Data)
{
    [JsonPropertyName("result")]
    public string Result => "ok";
}

public sealed record ApiError(
    [property: JsonPropertyName("message")] string Message)
{
    [JsonPropertyName("result")]
    public string Result => "error";
}
