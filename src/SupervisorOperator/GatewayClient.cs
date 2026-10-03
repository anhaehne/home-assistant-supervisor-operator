using System.Net.Http.Json;
using System.Text.Json;
using Supervisor.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace SupervisorOperator;

public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public GatewayClient(IConfiguration configuration)
    {
        var endpoint = configuration["Gateway:BaseUrl"] ?? "http://127.0.0.1:8081";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme != "http" || !string.IsNullOrEmpty(url.UserInfo) || url.AbsolutePath != "/")
            throw new InvalidOperationException("Gateway__BaseUrl must be an internal HTTP service root.");
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { BaseAddress = url, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-Gateway-Token", configuration["Gateway:Token"]);
    }

    public async Task<T> Read<T>(string route, CancellationToken cancellation) =>
        await client.GetFromJsonAsync<T>(route, Json, cancellation) ?? throw new InvalidDataException("Gateway returned no data.");

    public async Task<IActionResult> Proxy(string route, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(route, cancellation);
        return new ContentResult
        {
            Content = await response.Content.ReadAsStringAsync(cancellation),
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            StatusCode = (int)response.StatusCode
        };
    }

    public void Dispose() => client.Dispose();
}
