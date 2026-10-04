using k8s.Models;
using KubeOps.KubernetesClient;
using Supervisor.Contracts;

namespace SupervisorOperator.Lifecycle;

// The installed namespace-scoped client receives explicit read-only Node RBAC.
// Discovery uses declared Pod CIDRs, never guessed routes or ambient credentials.
public sealed class PodNetworkDiscovery(IKubernetesClient client)
{
    public async Task<string[]> Resolve(CancellationToken cancellation)
    {
        var nodes = (await client.ListAsync<V1Node>(cancellationToken: cancellation)).ToArray();
        if (nodes.Length == 0) throw new InvalidOperationException("Cannot discover Pod networks: the cluster returned no Nodes.");
        var networks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var declared = node.Spec?.PodCIDRs;
            string[] cidrs = declared is { Count: > 0 } ? declared.ToArray() :
                node.Spec?.PodCIDR is { Length: > 0 } fallbackCidr ? [fallbackCidr] : [];
            if (cidrs.Length == 0)
                throw new InvalidOperationException("Cannot discover Pod networks: every Node must declare a usable Pod CIDR.");
            try
            {
                // A bare address is valid for explicit proxy trust, but not a
                // Node's network declaration. Reject it rather than guessing.
                if (cidrs.Any(cidr => cidr is null || !cidr.Contains('/')))
                    throw new ArgumentException("A Node Pod CIDR must include a prefix.");
                foreach (var cidr in cidrs)
                    foreach (var normalized in HttpProxyValidation.Normalize([cidr])) networks.Add(normalized);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("Cannot discover Pod networks: a Node declares an invalid or unrestricted Pod CIDR.", exception);
            }
            if (networks.Count > 128)
                throw new InvalidOperationException("Cannot discover Pod networks: more than 128 distinct Pod CIDRs were returned.");
        }
        return networks.Order(StringComparer.Ordinal).ToArray();
    }
}
