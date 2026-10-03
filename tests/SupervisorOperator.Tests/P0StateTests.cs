using Microsoft.Extensions.Configuration;
using SupervisorOperator;

namespace SupervisorOperator.Tests;

public sealed class P0StateTests
{
    [Fact]
    public async Task MetricsNeverSelectAmbientHostingClusterCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Kubernetes:Namespace"] = "haso-p0" }).Build();
        using var metrics = new CoreMetrics(configuration);
        await Assert.ThrowsAsync<ApiValidationException>(() => metrics.Read(default));
    }
    [Fact]
    public async Task ConcurrentUpdatesSurviveAStoreReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haso-state-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supervisor:StateDirectory"] = directory }).Build();
        try
        {
            var store = new InstanceOptionsStore(configuration);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => store.Update(current => current with { Port = current.Port + 1 }, default)));
            Assert.Equal(105, new InstanceOptionsStore(configuration).Read().Port);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidStateIsNotSilentlyReplaced()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haso-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "instance-options.json"), "broken-json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Supervisor:StateDirectory"] = directory }).Build();
        try { Assert.Throws<System.Text.Json.JsonException>(() => new InstanceOptionsStore(configuration)); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CoreCountersAggregateDevicesAndExcludeLoopback()
    {
        var counters = CoreMetrics.Parse("usage_usec 1000\nMEMORY\n1024\n4096\nIO\n8:0 rbytes=100 wbytes=200 rios=1 wios=2\n8:1 rbytes=300 wbytes=400 rios=3 wios=4\nNETWORK\nInter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\nlo: 999 0 0 0 0 0 0 0 999 0 0 0 0 0 0 0\neth0: 123 0 0 0 0 0 0 0 456 0 0 0 0 0 0 0\n");
        Assert.Equal(new CoreMetrics.Counters(1000, 1024, 4096, 400, 600, 123, 456), counters);
    }
}
