using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Supervisor.Contracts;

namespace SupervisorOperator.Tests;

public sealed class HttpProxyGatewayTests
{
    private const string Token = "test-only-http-proxy-gateway-credential-32-bytes";
    private static readonly HttpProxySettings Desired = new(["10.244.69.17/32", "2001:db8::/64"]);

    [Fact]
    public async Task StagePreservesNativeSettingsAndRequiresRunningTrialBeforeConfirmation()
    {
        var native = new Native();
        var client = new CoreHttpProxyClient(native);
        var initial = await client.Status(Desired, default);
        Assert.False(initial.StableMatches);
        var staged = await client.Stage(Desired, default);
        Assert.True(staged.PendingMatches);
        Assert.False(staged.PendingActive);
        Assert.Equal(1, native.ConfigureCalls);
        Assert.Equal(80, native.Pending!["server_port"]!.GetValue<int>());
        Assert.Equal("/config/server.pem", native.Pending["ssl_certificate"]!.GetValue<string>());
        Assert.Equal("https://existing.example", native.Pending["cors_allowed_origins"]![0]!.GetValue<string>());
        Assert.False(native.Pending.ContainsKey("created_at"));
        Assert.False(native.Pending.ContainsKey("error"));
        await Assert.ThrowsAsync<HttpProxyConflictException>(() => client.Confirm(new(Desired.TrustedProxies, staged.DesiredFingerprint), default));
        Assert.Equal(0, native.PromoteCalls);
        native.Active = "pending";
        var confirmed = await client.Confirm(new(Desired.TrustedProxies, staged.DesiredFingerprint), default);
        Assert.True(confirmed.StableMatches);
        Assert.False(confirmed.PendingExists);
        Assert.Equal(1, native.PromoteCalls);
        await client.Stage(Desired, default);
        await client.Confirm(new(Desired.TrustedProxies, staged.DesiredFingerprint), default);
        Assert.Equal(1, native.ConfigureCalls);
        Assert.Equal(1, native.PromoteCalls);
    }

    [Fact]
    public async Task EquivalentCidrsAndRepeatedStageAreIdempotent()
    {
        var native = new Native();
        var client = new CoreHttpProxyClient(native);
        var first = await client.Stage(Desired, default);
        var second = await client.Stage(new(["2001:0db8:0:0::/64", "10.244.69.17", "10.244.69.17/32"]), default);
        Assert.Equal(first.DesiredFingerprint, second.DesiredFingerprint);
        Assert.Equal(1, native.ConfigureCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedOrFailedPendingIsNeverOverwrittenOrPromoted(bool failed)
    {
        var native = new Native();
        var client = new CoreHttpProxyClient(native);
        var state = await client.Stage(Desired, default);
        native.Active = "pending";
        if (failed) native.Pending!["error"] = "not_promoted";
        else native.Pending!["server_port"] = 81;
        await Assert.ThrowsAsync<HttpProxyConflictException>(() => client.Stage(Desired, default));
        await Assert.ThrowsAsync<HttpProxyConflictException>(() => client.Confirm(new(Desired.TrustedProxies, state.DesiredFingerprint), default));
        Assert.Equal(1, native.ConfigureCalls);
        Assert.Equal(0, native.PromoteCalls);
    }

    [Fact]
    public async Task FingerprintFencesNonProxyConfigurationChangesBeforePromotion()
    {
        var native = new Native();
        var client = new CoreHttpProxyClient(native);
        var staged = await client.Stage(Desired, default);
        native.Active = "pending";
        native.Stable["server_port"] = 81;
        await Assert.ThrowsAsync<HttpProxyConflictException>(() => client.Confirm(new(Desired.TrustedProxies, staged.DesiredFingerprint), default));
        Assert.Equal(0, native.PromoteCalls);
    }

    [Fact]
    public async Task RemovingProxyTrustRequiresItsOwnTrialAndKeepsOtherSettings()
    {
        var native = new Native();
        var client = new CoreHttpProxyClient(native);
        var enabled = await client.Stage(Desired, default);
        native.Active = "pending";
        await client.Confirm(new(Desired.TrustedProxies, enabled.DesiredFingerprint), default);
        var removed = await client.Stage(new([]), default);
        Assert.True(removed.PendingMatches);
        Assert.False(native.Pending!["use_x_forwarded_for"]!.GetValue<bool>());
        Assert.Empty(native.Pending["trusted_proxies"]!.AsArray());
        Assert.Equal("/config/server.pem", native.Pending["ssl_certificate"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("10.244.69.17/24")]
    [InlineData("2001:db8::1/64")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("010.244.69.17")]
    [InlineData("fe80::1%eth0")]
    [InlineData("http://proxy")]
    [InlineData("10.0.0.1/33")]
    [InlineData("::/129")]
    public void InvalidOrAmbiguousAddressesAreRejected(string value) => Assert.Throws<ArgumentException>(() => HttpProxyValidation.Normalize([value]));

    [Fact]
    public async Task EndpointsRequireGatewayCredentialAndRejectArbitraryConfiguration()
    {
        var native = new Native();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Token"] = Token });
        builder.Services.AddCoreGateway(builder.Configuration);
        builder.Services.AddSingleton<ICoreHttpConfigurationTransport>(native);
        await using var app = builder.Build();
        app.MapCoreGateway();
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var denied = await http.PostAsJsonAsync("/core/http/proxy-stage", new { trusted_proxies = Desired.TrustedProxies });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-Gateway-Token", Token);
        using var arbitrary = await http.PostAsJsonAsync("/core/http/proxy-stage", new { trusted_proxies = Desired.TrustedProxies, config = new { server_port = 81 } });
        Assert.Equal(HttpStatusCode.BadRequest, arbitrary.StatusCode);
        using var selectedTarget = await http.PostAsJsonAsync("/core/http/proxy-stage?target=/hassio/auth", new { trusted_proxies = Desired.TrustedProxies });
        Assert.Equal(HttpStatusCode.BadRequest, selectedTarget.StatusCode);
        using var staged = await http.PostAsJsonAsync("/core/http/proxy-stage", new { trusted_proxies = Desired.TrustedProxies });
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        Assert.Equal(1, native.ConfigureCalls);
    }

    private sealed class Native : ICoreHttpConfigurationTransport
    {
        public JsonObject Stable = JsonNode.Parse("""
            {"server_port":80,"ssl_certificate":"/config/server.pem","ssl_key":"/config/server.key",
             "server_host":["0.0.0.0"],"cors_allowed_origins":["https://existing.example"],
             "ip_ban_enabled":true,"login_attempts_threshold":5,"created_at":"old","error":null,"error_message":null}
            """)!.AsObject();
        public JsonObject? Pending;
        public string Active = "stable";
        public int ConfigureCalls;
        public int PromoteCalls;
        public Task<JsonObject> Read(CancellationToken cancellation) => Task.FromResult(new JsonObject
        {
            ["stable"] = Stable.DeepClone(), ["pending"] = Pending?.DeepClone(), ["active_config_type"] = Active
        });
        public Task Configure(JsonObject configuration, CancellationToken cancellation)
        {
            ConfigureCalls++; Pending = (JsonObject)configuration.DeepClone(); return Task.CompletedTask;
        }
        public Task Promote(CancellationToken cancellation)
        {
            PromoteCalls++; Stable = Pending!; Pending = null; Active = "stable"; return Task.CompletedTask;
        }
    }
}
