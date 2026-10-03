#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:-"$DEV_ROOT/.test-artifacts/interactive"}
mode=${2:-fresh}
[[ "$mode" == fresh || "$mode" == existing ]] || fail 'Browser mode must be fresh or existing.'
phase=${3:-all}
[[ "$phase" == all || "$phase" == http-migration ]] || fail 'E2E phase must be all or http-migration.'
mkdir -p "$artifacts"
dotnet build "$DEV_ROOT/tests/P0.E2E" --no-restore 9>&-
# Validate the client models directly inside the unmodified stock Core image.
kctl -n haso-p0 exec -i core-0 -c core -- python - < "$DEV_ROOT/tests/P0.E2E/upstream_contracts.py"
forward_core() {
    kctl_background "$artifacts/core-forward.log" -n haso-p0 port-forward --address 127.0.0.1 service/core 18123:80
    core_forward=$DEV_BACKGROUND_PID
    for attempt in {1..40}; do
        kill -0 "$core_forward" 2>/dev/null || fail 'Core port forwarding exited; inspect its artifact log.'
        if curl --silent --fail --max-time 2 http://127.0.0.1:18123/ >/dev/null; then return; fi
        sleep 1
    done
    fail 'Core loopback forwarding did not become ready.'
}
core_forward=''
stop_forward() {
    if [[ -n "$core_forward" ]]; then
        kill "$core_forward" 2>/dev/null || true
        wait "$core_forward" 2>/dev/null || true
        core_forward=''
    fi
}
trap stop_forward EXIT
if [[ "$phase" == all ]]; then
forward_core
dotnet "$DEV_ROOT/tests/P0.E2E/bin/Debug/net10.0/P0.E2E.dll" "$mode" "$DEV_STATE" "$artifacts" 2>&1 | guard redact "$DEV_STATE"
# Restart the API after persistent option changes and verify the same values.
kctl -n haso-p0 exec -i core-0 -c core -- python - <<'PY'
import asyncio,os,aiohttp
from aiohasupervisor import SupervisorClient
from aiohasupervisor.models import SupervisorOptions
async def main():
    async with aiohttp.ClientSession() as session:
        client=SupervisorClient('http://supervisor',os.environ['SUPERVISOR_TOKEN'],session=session)
        await client.supervisor.set_options(SupervisorOptions(timezone='Europe/Berlin',country='DE'))
asyncio.run(main())
PY
kctl -n haso-p0 scale deployment/supervisor --replicas=0
kctl -n haso-p0 wait --for=delete pod -l app=supervisor --timeout=120s
kctl -n haso-p0 scale deployment/supervisor --replicas=1
kctl -n haso-p0 rollout status deployment/supervisor --timeout=120s
kctl -n haso-p0 exec -i core-0 -c core -- python - <<'PY'
import asyncio,os,aiohttp
from aiohasupervisor import SupervisorClient
async def main():
    async with aiohttp.ClientSession() as session:
        client=SupervisorClient('http://supervisor',os.environ['SUPERVISOR_TOKEN'],session=session)
        info=await client.supervisor.info()
        assert info.timezone=='Europe/Berlin' and info.country=='DE'
    print('Persisted options survived the API restart.')
asyncio.run(main())
PY
# Restart the singleton Core sequentially, retaining its actual PVC and user.
stop_forward
kctl -n haso-p0 delete pod core-0 --wait=true --timeout=120s
kctl -n haso-p0 wait --for=jsonpath='{.status.readyReplicas}'=1 statefulset/core --timeout=360s
forward_core
dotnet "$DEV_ROOT/tests/P0.E2E/bin/Debug/net10.0/P0.E2E.dll" existing "$DEV_STATE" "$artifacts" 2>&1 | guard redact "$DEV_STATE"
kctl -n haso-p0 exec -i core-0 -c core -- python - < "$DEV_ROOT/tests/P0.E2E/upstream_contracts.py"
fi
# A separate legacy HTTP fixture replaces Core sequentially. Native Core must
# import YAML once, then retain its stored settings after YAML is removed.
stop_forward
kctl -n haso-p0 scale statefulset/core --replicas=0
kctl -n haso-p0 wait --for=delete pod/core-0 --timeout=120s
kctl -n haso-p0 delete pvc config-core-0 --wait=true --timeout=120s
kctl apply -f "$DEV_ROOT/deploy/p0-http-migration.yaml"
kctl -n haso-p0 scale statefulset/core --replicas=1
kctl -n haso-p0 wait --for=jsonpath='{.status.readyReplicas}'=1 statefulset/core --timeout=360s
forward_core
migration_artifacts="$artifacts/http-migration"
mkdir -p "$migration_artifacts"
dotnet "$DEV_ROOT/tests/P0.E2E/bin/Debug/net10.0/P0.E2E.dll" fresh "$DEV_STATE" "$migration_artifacts" http-migration 2>&1 | guard redact "$DEV_STATE"
kctl -n haso-p0 exec -i core-0 -c core -- python - <<'PY'
import asyncio,aiohttp,json,pathlib,socket,ipaddress
data=json.loads(pathlib.Path('/config/.storage/http').read_text())['data']
assert data['yaml_migration_done'] and data['pending'] is None
assert data['stable']['use_x_forwarded_for']
assert [ipaddress.ip_network(value) for value in data['stable']['trusted_proxies']]==[ipaddress.ip_network('127.0.0.1/32')]
async def main():
    # The Pod interface is not a trusted loopback proxy; reject its header.
    address=socket.gethostbyname('core')
    async with aiohttp.ClientSession() as session:
        async with session.get(f'http://{address}/api/',headers={'X-Forwarded-For':'198.51.100.42'}) as response:
            assert response.status==400, f'Untrusted proxy accepted: {response.status}'
asyncio.run(main())
print('Native YAML migration and untrusted-proxy rejection passed.')
PY
stop_forward
kctl apply -f "$DEV_ROOT/deploy/p0.yaml"
kctl -n haso-p0 delete pod core-0 --wait=true --timeout=120s
kctl -n haso-p0 wait --for=jsonpath='{.status.readyReplicas}'=1 statefulset/core --timeout=360s
forward_core
dotnet "$DEV_ROOT/tests/P0.E2E/bin/Debug/net10.0/P0.E2E.dll" existing "$DEV_STATE" "$migration_artifacts" http-migration 2>&1 | guard redact "$DEV_STATE"
kctl -n haso-p0 exec -i core-0 -c core -- python - < "$DEV_ROOT/tests/P0.E2E/upstream_contracts.py"
printf 'P0 E2E phase %s (initial account mode %s) passed.\n' "$phase" "$mode"
