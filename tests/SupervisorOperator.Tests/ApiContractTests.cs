using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SupervisorOperator;

namespace SupervisorOperator.Tests;

public sealed class ApiContractTests
{
    private const string Token = "test-only-core-credential-32-bytes-long";

    private static async Task<WebApplication> StartApi()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Supervisor:CoreToken"] = Token });
        builder.Services.AddSupervisorApi(builder.Configuration);
        var app = builder.Build();
        app.MapSupervisorApi();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task PingIsPublicAndIncludesEmptyDataObject()
    {
        await using var app = await StartApi();
        using var response = await app.GetTestClient().GetAsync("/supervisor/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("data").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("data").EnumerateObject());
    }

    [Fact]
    public async Task ControllerResponsesKeepSnakeCaseNamesAndNullMetadata()
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.GetAsync("/os/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("update_available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("version_latest").ValueKind);
        Assert.Empty(data.GetProperty("boot_slots").EnumerateObject());
        Assert.False(data.TryGetProperty("updateAvailable", out _));
    }

    [Theory]
    [InlineData("/core/options")]
    [InlineData("/homeassistant/options")]
    public async Task BothCoreControllerAliasesUpdateTheSameOptions(string path)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.PostAsync(path, new StringContent("{\"port\":8080,\"ssl\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = app.Services.GetRequiredService<InstanceOptionsStore>().Read();
        Assert.Equal(8080, options.Port);
        Assert.True(options.Ssl);
    }

    [Theory]
    [InlineData("/core/api/config?target=/hassio/auth")]
    [InlineData("/homeassistant/api/?target=/hassio/auth")]
    public async Task ControllerProxyRejectsQueryTargetsWithoutCallingTheGateway(string path)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Proxy query parameters are unavailable", json.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("X-Supervisor-Token", "")]
    [InlineData("X-Hassio-Key", "")]
    [InlineData("Authorization", "Bearer ")]
    public async Task OnboardingUpdateReturnsBadRequestInsteadOfStartingAnUpgrade(string header, string prefix)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(header, prefix + Token);
        using var response = await client.PostAsync("/supervisor/update", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", json.RootElement.GetProperty("result").GetString());
        Assert.StartsWith("No supervisor update available - ", json.RootElement.GetProperty("message").GetString());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-credential")]
    public async Task UpdatesRequireCoreCredential(string? token)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        if (token is not null) client.DefaultRequestHeaders.Add("X-Supervisor-Token", token);
        using var response = await client.PostAsync("/supervisor/update", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ModernHeaderTakesPrecedenceOverValidLegacyCredential()
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", "wrong");
        client.DefaultRequestHeaders.Add("X-Hassio-Key", Token);
        using var response = await client.PostAsync("/supervisor/update", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("{", "Invalid json")]
    [InlineData("[]", "Expected an object")]
    [InlineData("{\"unknown\":true}", "Invalid update options")]
    public async Task InvalidUpdateBodiesFailWithErrorEnvelope(string body, string message)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.PostAsync("/supervisor/update", new StringContent(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(message, json.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/hardware")]
    [InlineData("/v2/supervisor/ping")]
    public async Task DeferredRoutesDoNotClaimSuccess(string path)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/info")]
    [InlineData("/supervisor/info")]
    [InlineData("/host/info")]
    [InlineData("/os/info")]
    [InlineData("/store")]
    [InlineData("/addons")]
    [InlineData("/resolution/info")]
    public async Task IndependentStartupReadsWorkWithoutCoreOrGateway(string path)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("result").GetString());
        if (path == "/info")
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("hassos").ValueKind);
            Assert.False(document.RootElement.GetProperty("data").GetProperty("supported").GetBoolean());
        }
    }

    [Theory]
    [InlineData("/supervisor/options", "{\"timezone\":\"Invalid/Timezone\"}")]
    [InlineData("/supervisor/options", "{\"country\":true}")]
    [InlineData("/core/options", "{\"port\":0}")]
    [InlineData("/core/options", "{\"ssl\":\"false\"}")]
    [InlineData("/core/options", "{\"refresh_token\":\"privileged-token\"}")]
    public async Task InvalidCallbacksDoNotChangeStoredOptions(string path, string body)
    {
        await using var app = await StartApi();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Supervisor-Token", Token);
        var store = app.Services.GetRequiredService<InstanceOptionsStore>();
        var before = store.Read();
        using var response = await client.PostAsync(path, new StringContent(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, store.Read());
    }
}
