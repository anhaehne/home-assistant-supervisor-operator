using System.Net.Http.Json;
using System.Text.Json;
using Supervisor.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace SupervisorOperator;

public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public GatewayClient(IConfiguration configuration, HttpMessageHandler? handler = null)
    {
        var endpoint = configuration["Gateway:BaseUrl"] ?? "http://127.0.0.1:8081";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme != "http" || !string.IsNullOrEmpty(url.UserInfo) || url.AbsolutePath != "/")
            throw new InvalidOperationException("Gateway__BaseUrl must be an internal HTTP service root.");
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { BaseAddress = url, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-Gateway-Token", configuration["Gateway:Token"]);
    }

    public async Task<T> Read<T>(string route, CancellationToken cancellation) =>
        await client.GetFromJsonAsync<T>(route, Json, cancellation) ?? throw new InvalidDataException("Gateway returned no data.");

    public async Task<HttpProxyState> HttpProxy(string action, HttpProxySettings settings, string? fingerprint, CancellationToken cancellation)
    {
        if (action is not ("status" or "stage" or "confirm")) throw new ArgumentException("Unsupported proxy action", nameof(action));
        using var response = action == "confirm"
            ? await client.PostAsJsonAsync($"/core/http/proxy-{action}", new HttpProxyConfirmation(settings.TrustedProxies, fingerprint ?? throw new ArgumentNullException(nameof(fingerprint))), Json, cancellation)
            : await client.PostAsJsonAsync($"/core/http/proxy-{action}", settings, Json, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HttpProxyState>(Json, cancellation) ?? throw new InvalidDataException("Gateway returned no proxy state.");
    }

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
