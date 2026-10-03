using System.Text.Json;

namespace SupervisorOperator.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void EveryPinnedRouteIsClassifiedAndUnique()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline.json")));
        var routes = document.RootElement.GetProperty("routes").EnumerateArray().ToArray();
        Assert.True(routes.Length > 100);
        var identities = routes.Select(route => string.Join(' ', route.GetProperty("version").GetString(),
            route.GetProperty("method").GetString(), route.GetProperty("path").GetString())).ToArray();
        Assert.Equal(identities.Length, identities.Distinct().Count());
        foreach (var route in routes)
        {
            Assert.Contains(route.GetProperty("status").GetString(), new[] { "supported", "deferred", "unavailable" });
            if (route.GetProperty("version").GetString() == "v2") Assert.Equal("unavailable", route.GetProperty("status").GetString());
        }
        Assert.NotEmpty(document.RootElement.GetProperty("client_models").EnumerateObject());
        Assert.NotEmpty(document.RootElement.GetProperty("bundled_frontend").EnumerateArray());
        Assert.Equal("0.6.0", document.RootElement.GetProperty("baseline").GetProperty("client").GetString());
    }
}
