using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using SupervisorOperator.Lifecycle;

namespace SupervisorOperator.Tests;

public sealed class PodNetworkDiscoveryTests
{
    [Fact]
    public async Task DeclaredDualStackNetworksAreNormalizedDeduplicatedAndOrdered()
    {
        using var fixture = new Fixture([
            Node("worker-b", "10.244.2.0/24", ["10.244.2.0/24", "2001:0db8:0000:2::/64"]),
            Node("worker-a", "10.244.1.0/24", ["10.244.1.0/24", "2001:db8:0:1::/64"]),
            Node("worker-c", "10.244.1.0/24", ["10.244.1.0/24", "2001:db8:0:1::/64"])
        ]);
        var networks = await fixture.Discovery.Resolve(default);
        Assert.Equal(new[] { "10.244.1.0/24", "10.244.2.0/24", "2001:db8:0:1::/64", "2001:db8:0:2::/64" }, networks);
        Assert.Equal(1, fixture.Api.Reads);
    }

    [Fact]
    public async Task SingularPodCidrIsUsedWhenPluralDeclarationsAreAbsentOrEmpty()
    {
        using var fixture = new Fixture([
            Node("worker-a", "10.244.1.0/24", null), Node("worker-b", "10.244.2.0/24", [])
        ]);
        Assert.Equal(new[] { "10.244.1.0/24", "10.244.2.0/24" }, await fixture.Discovery.Resolve(default));
    }

    [Fact]
    public async Task NoNodesCannotBeInterpretedAsSuccessfulDiscovery()
    {
        using var fixture = new Fixture([]);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Discovery.Resolve(default));
        Assert.Contains("no Nodes", failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.244.1.0")]
    [InlineData("10.244.1.17/24")]
    [InlineData("2001:db8::1/64")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("not-a-network")]
    public async Task EveryNodeMustDeclareUsableRestrictedNetworks(string? invalid)
    {
        using var fixture = new Fixture([
            Node("valid-worker", "10.244.1.0/24", null), Node("invalid-worker", invalid, null)
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Discovery.Resolve(default));
    }

    [Fact]
    public async Task InvalidPluralDeclarationDoesNotSilentlyFallBackToValidSingularNetwork()
    {
        using var fixture = new Fixture([Node("worker", "10.244.1.0/24", ["10.244.1.0/24", "0.0.0.0/0"])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Discovery.Resolve(default));
    }

    [Fact]
    public async Task MissingNodeSpecFailsClosed()
    {
        using var fixture = new Fixture([new V1Node { Metadata = new() { Name = "missing-spec" } }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Discovery.Resolve(default));
    }

    [Fact]
    public async Task UnionLargerThanGatewayLimitFailsWithoutReturningPartialTrust()
    {
        using var fixture = new Fixture(Enumerable.Range(0, 129).Select(index => Node("worker-" + index, $"10.244.{index}.0/24", null)).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Discovery.Resolve(default));
    }

    private static V1Node Node(string name, string? cidr, IList<string>? cidrs) => new()
    {
        Metadata = new() { Name = name }, Spec = new() { PodCIDR = cidr, PodCIDRs = cidrs }
    };

    private sealed class Fixture : IDisposable
    {
        public NodeApi Api { get; }
        public PodNetworkDiscovery Discovery { get; }
        private readonly KubeOps.KubernetesClient.KubernetesClient client;
        public Fixture(V1Node[] nodes)
        {
            Api = new(nodes);
            var configuration = new KubernetesClientConfiguration { Host = "http://unit.invalid" };
            client = new(configuration, new Kubernetes(configuration, Api));
            Discovery = new(client);
        }
        public void Dispose() => client.Dispose();
    }

    // Only an exact cluster Node list is served. Any namespace, pod, or secret
    // request fails, and no socket or ambient kubeconfig is involved.
    private sealed class NodeApi(V1Node[] nodes) : DelegatingHandler
    {
        public int Reads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/nodes", request.RequestUri!.AbsolutePath);
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(KubernetesJson.Serialize(new V1NodeList { Items = nodes.ToList(), Metadata = new() }), Encoding.UTF8, "application/json")
            });
        }
    }
}
