#!/usr/bin/env bash
# Installed-chart acceptance using actual, separately addressed reverse proxies.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:-"$DEV_ROOT/.test-artifacts/p1-interactive"}
mode=${2:-existing}
[[ "$mode" == existing || "$mode" == fresh ]] || fail 'Proxy acceptance mode must be existing or fresh.'
mkdir -p "$artifacts"
ns=haso-p1
release=haso
if [[ "$mode" == fresh ]]; then
    # Singleton cluster policies require the earlier production instance to be
    # shut down normally before this independent empty-PVC installation.
    hctl uninstall haso -n haso-p1 --wait --timeout 10m > "$artifacts/proxy-main-uninstall.log"
    kctl -n haso-p1 wait --for=delete pod/core-0 --timeout=120s
    ns=haso-proxy-fresh
    release=haso-proxy-fresh
    kctl create namespace "$ns"
    kctl -n "$ns" create secret generic credentials --from-file="core-token=$DEV_STATE/core-token" --from-file="gateway-token=$DEV_STATE/gateway-token"
fi

for proxy in p1-proxy-a p1-proxy-b; do
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
done
proxy_a=$(kctl -n "$ns" get pod/p1-proxy-a -o jsonpath='{.status.podIP}')
proxy_b=$(kctl -n "$ns" get pod/p1-proxy-b -o jsonpath='{.status.podIP}')
[[ -n "$proxy_a" && -n "$proxy_b" && "$proxy_a" != "$proxy_b" ]] || fail 'Reverse proxy fixtures require distinct Pod IPs.'
# The sealed kind fixture uses IPv4; exact /32s deliberately exclude other Pods.
[[ "$proxy_a" != *:* && "$proxy_b" != *:* ]] || fail 'The acceptance fixture requires its configured IPv4 Pod network.'

healthy() {
    local generation uid
    generation=$(kctl -n "$ns" get homeassistantinstance/home-assistant -o jsonpath='{.metadata.generation}')
    kctl -n "$ns" wait --for=jsonpath='{.status.observedGeneration}'="$generation" homeassistantinstance/home-assistant --timeout=600s
    kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True homeassistantinstance/home-assistant --timeout=600s
    kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
    uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
    kctl -n "$ns" wait --for=jsonpath='{.status.podUid}'="$uid" homeassistantinstance/home-assistant --timeout=120s
}
assert_native() {
    # Inspect only. Neither tests nor installation mutate Core's private store.
    kctl -n "$ns" exec -i core-0 -c core -- python - "$1" <<'PY'
import ipaddress, json, pathlib, sys
expected = [ipaddress.ip_network(value) for value in json.loads(sys.argv[1])]
data = json.loads(pathlib.Path('/config/.storage/http').read_text())['data']
assert data['yaml_migration_done'], 'Native YAML migration is not complete'
assert data['pending'] is None, 'The trial HTTP configuration was not promoted'
stable = data['stable']
assert stable['server_port'] == 80, 'Managed proxy setting changed the Core port'
assert stable.get('use_x_forwarded_for', False) == bool(expected), 'Forwarded HTTP setting is stale'
assert [ipaddress.ip_network(value) for value in stable.get('trusted_proxies', [])] == expected, 'Native allowlist differs from Helm intent'
PY
}
proxy_request() {
    local proxy=$1 expected=$2 phase=$3
    # An actual aiohttp reverse proxy forwards through Service/core from its own
    # Pod IP. A loopback client of this proxy is not the source trusted by Core.
    kctl -n "$ns" exec -i "$proxy" -c proxy -- python - "$expected" <<'PY' > "$artifacts/proxy-$mode-$phase-$proxy.json"
import asyncio, json, sys
import aiohttp
from aiohttp import web
expected = int(sys.argv[1])
async def main():
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=20)) as upstream:
        async def forward(request):
            headers = {'X-Forwarded-For': request.headers.get('X-Forwarded-For', request.remote)}
            async with upstream.get('http://core/', headers=headers) as response:
                return web.Response(status=response.status, body=await response.read())
        app = web.Application()
        app.router.add_get('/', forward)
        runner = web.AppRunner(app)
        await runner.setup()
        await web.TCPSite(runner, '127.0.0.1', 18081).start()
        try:
            async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=20)) as client:
                async with client.get('http://127.0.0.1:18081/', headers={'X-Forwarded-For': '198.51.100.42'}) as response:
                    forwarded = response.status
                    assert forwarded == expected, f'Proxy status {forwarded}, expected {expected}'
                async with client.get('http://127.0.0.1:18081/', headers={'X-Forwarded-For': 'invalid-client-address'}) as response:
                    assert response.status == 400, f'Invalid forwarded client accepted: HTTP {response.status}'
            async with upstream.get('http://core/') as response:
                plain = response.status
                assert plain == 200, f'Plain Core request rejected: HTTP {plain}'
            print(json.dumps({'forwardedStatus': forwarded, 'plainStatus': plain, 'invalidForwardedClientStatus': 400}))
        finally:
            await runner.cleanup()
asyncio.run(main())
PY
}
snapshot_data() {
    kctl -n "$ns" exec -i core-0 -c core -- python - <<'PY'
import hashlib, json, pathlib
root = pathlib.Path('/config')
auth = root / '.storage/auth'
users = json.loads(auth.read_text())['data']['users'] if auth.exists() else []
print(json.dumps({'configuration': hashlib.sha256((root / 'configuration.yaml').read_bytes()).hexdigest(), 'users': sorted((user['id'], user['name'], user['is_owner']) for user in users)}, sort_keys=True))
PY
}
upgrade_proxy() {
    local desired=$1 old_uid
    old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
    hctl upgrade "$release" "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n "$ns" --reuse-values \
        --set ingress.trustPodNetwork=false --set-json "ingress.trustedProxies=$desired" --wait --timeout 5m > "$artifacts/proxy-$mode-upgrade-$upgrade_index.log"
    healthy
    if [[ "$desired" != null ]]; then
        [[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') != "$old_uid" ]] || fail 'Managed proxy change did not trial a restarted Core.'
        assert_native "$desired"
    else
        [[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') == "$old_uid" ]] || fail 'Unmanaged proxy intent restarted Core.'
    fi
    [[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}') == "$pvc_identity" ]] || fail 'Managed proxy change replaced the PVC/PV.'
    [[ $(snapshot_data) == "$data_before" ]] || fail 'Managed proxy change altered user data or configuration.yaml.'
    [[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Managed proxy change lost retained data.'
    upgrade_index=$((upgrade_index + 1))
}

if [[ "$mode" == existing ]]; then
    healthy
    pvc_identity=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}')
    data_before=$(snapshot_data)
    upgrade_index=0
    # The recommended chart defaults trust the discovered Pod network. Disable
    # that mode explicitly before testing exact ingress-source IP allowlists.
    upgrade_proxy '[]'
    assert_native '[]'
    # These preconditions prove this is a previously onboarded PVC, not an
    # initial YAML migration dressed up as an upgrade acceptance test.
    kctl -n "$ns" exec -i core-0 -c core -- python - <<'PY'
import json, pathlib
assert any(user['is_owner'] for user in json.loads(pathlib.Path('/config/.storage/auth').read_text())['data']['users']), 'Existing-PVC acceptance requires an onboarded owner'
PY
    proxy_request p1-proxy-a 400 initial
    proxy_request p1-proxy-b 400 initial
    upgrade_proxy "[\"$proxy_a/32\"]"
    proxy_request p1-proxy-a 200 allow-a
    proxy_request p1-proxy-b 400 allow-a
    upgrade_proxy "[\"$proxy_b/32\"]"
    proxy_request p1-proxy-a 400 allow-b
    proxy_request p1-proxy-b 200 allow-b
    upgrade_proxy '[]'
    proxy_request p1-proxy-a 400 revoked
    proxy_request p1-proxy-b 400 revoked
    upgrade_proxy null
    assert_native '[]'
    kctl -n "$ns" delete pod/p1-proxy-a pod/p1-proxy-b --wait=true --timeout=120s
else
    hctl install "$release" "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n "$ns" \
        --set operatorImage=haso/operator:p0 --set gatewayImage=haso/gateway:p0 \
        --set imagePullPolicy=Never --set coreImage=ghcr.io/home-assistant/home-assistant:2026.9.4 \
        --set storageClassName=standard --set "operatorNode=$(cluster_name)-control-plane" \
        --set 'operatorTolerations[0].key=node-role.kubernetes.io/control-plane' \
        --set 'operatorTolerations[0].operator=Exists' --set 'operatorTolerations[0].effect=NoSchedule' \
        --set ingress.trustPodNetwork=false \
        --set-json "ingress.trustedProxies=[\"$proxy_a/32\"]" --wait --timeout 5m > "$artifacts/proxy-fresh-install.log"
    healthy
    assert_native "[\"$proxy_a/32\"]"
    proxy_request p1-proxy-a 200 installed
    proxy_request p1-proxy-b 400 installed
    pvc_identity=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}')
    kctl -n "$ns" exec core-0 -c core -- sh -ec 'printf retained > /config/p1-retained.txt'
    data_before=$(snapshot_data)
    old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
    kctl -n "$ns" delete pod/core-0 --wait=true --timeout=120s
    for attempt in {1..60}; do
        new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
        [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
        sleep 1
    done
    [[ -n "$new_uid" && "$new_uid" != "$old_uid" ]] || fail 'Fresh Core did not restart.'
    healthy
    assert_native "[\"$proxy_a/32\"]"
    proxy_request p1-proxy-a 200 restarted
    proxy_request p1-proxy-b 400 restarted
    [[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}') == "$pvc_identity" ]] || fail 'Fresh restart replaced the PVC/PV.'
    [[ $(snapshot_data) == "$data_before" ]] || fail 'Fresh restart altered user data or configuration.yaml.'
    [[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Fresh restart lost retained data.'
    hctl uninstall "$release" -n "$ns" --wait --timeout 10m > "$artifacts/proxy-fresh-uninstall.log"
    kctl -n "$ns" wait --for=delete pod/core-0 --timeout=120s
    [[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.status.phase}') == Bound ]] || fail 'Fresh Helm uninstall did not retain its PVC.'
    kctl -n "$ns" delete pvc/instance-data --wait=true --timeout=120s
    kctl delete namespace "$ns" --wait=true --timeout=120s
fi
printf 'P1 %s ingress proxy acceptance passed: actual forwarded requests, exact source allowlist, revocation, promoted native state and retained data.\n' "$mode"
