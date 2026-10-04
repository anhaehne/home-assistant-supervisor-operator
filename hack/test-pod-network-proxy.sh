#!/usr/bin/env bash
# Verify recommended ingress defaults discover and reconcile the Pod CIDRs.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:-"$DEV_ROOT/.test-artifacts/p1-interactive"}
mkdir -p "$artifacts"
ns=haso-p1
owner=$(cluster_name)
proxy=p1-pod-network-proxy
declare -A owned_nodes=()
delete_owned_node() {
    local name=$1 actual
    actual=$(kctl get node/"$name" -o jsonpath='{.metadata.uid}/{.metadata.labels.ha-operator\.io/test-owner}') || return
    [[ "$actual" == "${owned_nodes[$name]}/$owner" ]] || { printf 'Refusing to delete changed synthetic node %s.\n' "$name" >&2; return 1; }
    kctl delete node/"$name" --wait=true --timeout=120s || return
    unset 'owned_nodes[$name]'
}
cleanup() {
    local result=$? name
    trap - EXIT
    # These are explicitly owned API-only fixtures, never runtime containers or
    # sealed kind nodes. Preserve the proxy Pod on failure for suite diagnostics.
    for name in "${!owned_nodes[@]}"; do delete_owned_node "$name" || result=1; done
    exit "$result"
}
trap cleanup EXIT
subject=system:serviceaccount:$ns:supervisor
[[ $(kctl auth can-i get nodes --as="$subject") == yes ]] || fail 'Recommended mode lacks Node get permission.'
[[ $(kctl auth can-i list nodes --as="$subject") == yes ]] || fail 'Recommended mode lacks Node list permission.'
[[ $(kctl auth can-i update nodes --as="$subject" || true) == no ]] || fail 'Pod-network discovery must not grant Node mutation.'
[[ $(kctl auth can-i get secrets -n default --as="$subject" || true) == no ]] || fail 'Pod-network discovery must not grant cross-namespace Secret access.'
kctl -n "$ns" create -f - <<EOF_PROXY
apiVersion: v1
kind: Pod
metadata: {name: $proxy}
spec:
  automountServiceAccountToken: false
  restartPolicy: Never
  securityContext: {runAsUser: 1000, runAsGroup: 1000}
  containers:
    - name: proxy
      image: ghcr.io/home-assistant/home-assistant:2026.9.4
      imagePullPolicy: Never
      command: [python, -c, "import time; time.sleep(86400)"]
      resources:
        requests: {cpu: 10m, memory: 32Mi}
        limits: {cpu: 250m, memory: 128Mi}
      securityContext:
        allowPrivilegeEscalation: false
        readOnlyRootFilesystem: true
        capabilities: {drop: [ALL]}
EOF_PROXY
kctl -n "$ns" wait --for=condition=Ready pod/"$proxy" --timeout=120s
pvc_identity=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}')

assert_networks() {
    local old_uid=${1:-} phase=$2 nodes current_uid generation matches=false
    nodes=$(kctl get nodes -o json)
    printf '%s\n' "$nodes" > "$artifacts/pod-network-$phase-nodes.json"
    # Discovery is driven by periodic reconciliation, not an instance generation change.
    # Wait for the new promoted native list and its new Pod before trusting Ready.
    for attempt in {1..180}; do
        current_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
        if [[ -n "$current_uid" && ( -z "$old_uid" || "$current_uid" != "$old_uid" ) ]]; then
            if kctl -n "$ns" exec -i core-0 -c core -- python - "$nodes" <<'PY' > "$artifacts/pod-network-$phase-native.json" 2> "$artifacts/pod-network-$phase-wait.log"
import ipaddress, json, pathlib, sys
nodes = json.loads(sys.argv[1])['items']
expected = set()
for node in nodes:
    spec = node['spec']
    cidrs = spec.get('podCIDRs') or ([spec['podCIDR']] if spec.get('podCIDR') else [])
    assert cidrs, f"Node {node['metadata']['name']} has no Pod network"
    expected.update(str(ipaddress.ip_network(value, strict=False)) for value in cidrs)
assert expected, 'No Pod networks discovered'
data = json.loads(pathlib.Path('/config/.storage/http').read_text())['data']
assert data['pending'] is None, 'Native trial is not promoted'
assert data['stable']['use_x_forwarded_for'], 'Recommended Pod-network proxy support is disabled'
actual = {str(ipaddress.ip_network(value, strict=False)) for value in data['stable']['trusted_proxies']}
assert actual == expected, f'Native networks {actual} differ from all Node CIDRs {expected}'
print(json.dumps({'networks': sorted(actual), 'pending': None, 'yamlMigrationDone': data['yaml_migration_done']}))
PY
            then
                matches=true
                break
            fi
        fi
        sleep 2
    done
    [[ "$matches" == true ]] || fail "Pod-network proxy reconciliation did not converge during $phase."
    generation=$(kctl -n "$ns" get homeassistantinstance/home-assistant -o jsonpath='{.metadata.generation}')
    kctl -n "$ns" wait --for=jsonpath='{.status.observedGeneration}'="$generation" homeassistantinstance/home-assistant --timeout=600s
    kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True homeassistantinstance/home-assistant --timeout=600s
    kctl -n "$ns" wait --for=jsonpath='{.status.podUid}'="$current_uid" homeassistantinstance/home-assistant --timeout=120s
    [[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}') == "$pvc_identity" ]] || fail 'Pod-network membership reconciliation replaced the PVC/PV.'
    [[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Pod-network membership reconciliation lost retained data.'
    # A real proxy process in another Pod forwards client traffic to Core. This
    # proves CIDR discovery covers the actual peer, not merely a stored list.
    kctl -n "$ns" exec -i "$proxy" -c proxy -- python - <<'PY' > "$artifacts/pod-network-$phase-forwarded.json"
import asyncio, json
import aiohttp
from aiohttp import web
async def main():
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=20)) as upstream:
        async def forward(request):
            async with upstream.get('http://core/', headers={'X-Forwarded-For': '198.51.100.42'}) as response:
                return web.Response(status=response.status, body=await response.read())
        app = web.Application()
        app.router.add_get('/', forward)
        runner = web.AppRunner(app)
        await runner.setup()
        await web.TCPSite(runner, '127.0.0.1', 18081).start()
        try:
            async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=20)) as client:
                async with client.get('http://127.0.0.1:18081/') as response:
                    assert response.status == 200, f'Actual Pod-network proxy rejected: HTTP {response.status}'
                    print(json.dumps({'forwardedStatus': response.status}))
        finally:
            await runner.cleanup()
asyncio.run(main())
PY
}
create_test_node() {
    local name=$1 cidr=$2
    # Unschedulable, unready API-only fixtures exercise changing discovery
    # membership without pretending a runtime node exists or weakening seals.
    kctl create -f - <<EOF_NODE
apiVersion: v1
kind: Node
metadata:
  name: $name
  labels: {ha-operator.io/test-owner: $owner}
spec:
  unschedulable: true
  podCIDR: $cidr
  podCIDRs: [$cidr]
  taints:
    - {key: ha-operator.io/test-fixture, value: pod-network, effect: NoSchedule}
EOF_NODE
    owned_nodes[$name]=$(kctl get node/"$name" -o jsonpath='{.metadata.uid}')
}
assert_networks '' default
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
create_test_node haso-proxy-network-a 198.18.0.0/24
assert_networks "$old_uid" add-a
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
create_test_node haso-proxy-network-b 198.18.1.0/24
assert_networks "$old_uid" add-b
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
delete_owned_node haso-proxy-network-a
assert_networks "$old_uid" remove-a
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
delete_owned_node haso-proxy-network-b
assert_networks "$old_uid" remove-b
kctl -n "$ns" delete pod/"$proxy" --wait=true --timeout=120s
printf '%s\n' 'P1 default Pod-network proxy discovery, real forwarded traffic, membership additions/removals, native promotion and retained PVC/data passed.'
