using System.Text.Json;
using System.Text.Json.Nodes;
using SupervisorOperator.Addons;

namespace SupervisorOperator.Tests;

public sealed class AddonCapabilityPolicyTests
{
    private const string Minimal = """
        {"name":"Example","description":"Example app","slug":"example","version":"1.2.3","arch":["amd64","aarch64"],"boot":"manual",
         "image":"ghcr.io/example/{arch}-example","init":false,"options":{},"schema":{}}
        """;

    private static AddonCapabilityResult Evaluate(string json, string architecture = "amd64")
    {
        using var document = JsonDocument.Parse(json);
        return AddonCapabilityPolicy.Evaluate(document.RootElement, architecture);
    }

    [Fact]
    public void ResolvesArchitectureAndExactVersionForPrebuiltImage()
    {
        var result = Evaluate(Minimal, "aarch64");
        Assert.True(result.CompatibleWithProfile);
        Assert.Equal("ghcr.io/example/aarch64-example:1.2.3", result.ImageReference);
    }

    [Theory]
    [InlineData("host_network", "true")]
    [InlineData("docker_api", "true")]
    [InlineData("host_pid", "true")]
    [InlineData("stdin", "true")]
    [InlineData("auth_api", "true")]
    [InlineData("homeassistant_api", "true")]
    [InlineData("ingress", "true")]
    [InlineData("devices", "[\"/dev/ttyUSB0\"]")]
    [InlineData("privileged", "[\"SYS_ADMIN\"]")]
    [InlineData("map", "[\"homeassistant_config:rw\"]")]
    [InlineData("services", "[\"mqtt:provide\"]")]
    [InlineData("discovery", "[\"mqtt\"]")]
    [InlineData("ports", "{\"1883/tcp\":1883}")]
    [InlineData("environment", "{\"SUPERVISOR_TOKEN\":\"override\"}")]
    [InlineData("apparmor", "false")]
    [InlineData("hassio_role", "\"admin\"")]
    [InlineData("startup", "\"system\"")]
    [InlineData("boot", "\"auto\"")]
    [InlineData("watchdog", "\"tcp://[HOST]:[PORT:1883]\"")]
    [InlineData("future_runtime_access", "true")]
    [InlineData("host_network", "\"false\"")]
    [InlineData("options", "[]")]
    [InlineData("schema", "{\"password\":\"password\"}")]
    [InlineData("homeassistant", "\"2026.9.4\"")]
    public void UnsupportedOrMalformedRequirementsFailClosed(string field, string value)
    {
        var manifest = JsonNode.Parse(Minimal)!.AsObject();
        manifest[field] = JsonNode.Parse(value);
        var result = Evaluate(manifest.ToJsonString());
        Assert.False(result.CompatibleWithProfile);
        Assert.Null(result.ImageReference);
        Assert.Contains(result.Issues, issue => issue.Field == field && issue.Message.Length > 0);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("version")]
    [InlineData("arch")]
    [InlineData("init")]
    [InlineData("boot")]
    [InlineData("description")]
    public void MissingPrerequisitesCannotBecomeCompatible(string field)
    {
        var manifest = JsonNode.Parse(Minimal)!.AsObject();
        manifest.Remove(field);
        Assert.Contains(Evaluate(manifest.ToJsonString()).Issues, issue => issue.Field == field);
    }

    [Theory]
    [InlineData("ghcr.io/example/addon:latest")]
    [InlineData("ghcr.io/example/addon@sha256:1234")]
    [InlineData("https://ghcr.io/example/addon")]
    [InlineData("ghcr.io/example/{unknown}-addon")]
    [InlineData("ghcr.io/example/addon\n")]
    public void InvalidImageRepositoriesAreRejected(string image)
    {
        var manifest = JsonNode.Parse(Minimal)!.AsObject();
        manifest["image"] = image;
        Assert.Contains(Evaluate(manifest.ToJsonString()).Issues, issue => issue.Field == "image");
    }

    [Theory]
    [InlineData("armv7")]
    [InlineData("x86_64")]
    public void ArchitectureMustBeKnownAndDeclared(string architecture)
        => Assert.False(Evaluate(Minimal, architecture).CompatibleWithProfile);

    [Fact]
    public void DuplicateFieldsCannotHideRequirements()
    {
        var json = Minimal.Replace("\"init\":false", "\"init\":true,\"init\":false", StringComparison.Ordinal);
        Assert.Contains(Evaluate(json).Issues, issue => issue.Field == "init");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonObjectManifestProducesActionableFailure(string json)
        => Assert.Contains(Evaluate(json).Issues, issue => issue.Field == "manifest");
}
