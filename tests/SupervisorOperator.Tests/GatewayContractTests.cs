using System.Net;
using CoreGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SupervisorOperator.Tests;

public sealed class GatewayContractTests
{
    private const string Token = "test-only-gateway-credential-32-bytes-long";

    private static async Task<WebApplication> StartGateway()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Token"] = Token,
            ["Gateway:Socket"] = Path.Combine(Path.GetTempPath(), "haso-absent-" + Guid.NewGuid().ToString("N") + ".sock")
        });
        builder.Services.AddCoreGateway(builder.Configuration);
        var app = builder.Build();
        app.MapCoreGateway();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task GatewayLivenessRemainsPublic()
    {
        await using var app = await StartGateway();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/core/status", null, HttpStatusCode.Unauthorized)]
    [InlineData("/core/config", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("/core/config?target=/hassio/auth", Token, HttpStatusCode.BadRequest)]
    [InlineData("/core/api/hassio/auth", Token, HttpStatusCode.NotFound)]
    [InlineData("/supervisor/ping", Token, HttpStatusCode.NotFound)]
    [InlineData("/core/status", Token, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/core/config", Token, HttpStatusCode.ServiceUnavailable)]
    public async Task GatewayControllerPreservesCredentialAndSocketBoundaries(string path, string? credential, HttpStatusCode expected)
    {
        await using var app = await StartGateway();
        using var client = app.GetTestClient();
        if (credential is not null) client.DefaultRequestHeaders.Add("X-Gateway-Token", credential);
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
    }
}
