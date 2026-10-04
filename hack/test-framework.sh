#!/usr/bin/env bash
# P1 framework acceptance in the recorded project-owned cluster only.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
probe=frameworkprobes.spikes.ha-operator.io
identity=system:serviceaccount:haso-p0:supervisor

expect_denial() {
    local result=0 answer
    answer=$(kctl auth can-i "$@" --as="$identity") || result=$?
    [[ "$result" == 1 && "$answer" == no ]] || fail "Expected RBAC denial: $* (exit $result / $answer)"
}
for verb in get list watch; do
    kctl auth can-i "$verb" "$probe" -n haso-p0 --as="$identity" | rg -qx yes
    expect_denial "$verb" "$probe" -n default
    expect_denial "$verb" "$probe" --all-namespaces
done
kctl auth can-i update "$probe" --subresource=status -n haso-p0 --as="$identity" | rg -qx yes
kctl auth can-i update configmaps/framework-probe-state -n haso-p0 --as="$identity" | rg -qx yes
expect_denial update configmaps/core-config -n haso-p0
expect_denial create configmaps -n haso-p0
expect_denial create "$probe" -n haso-p0
expect_denial update statefulsets/core -n haso-p0
expect_denial get secrets -n haso-p0

expect_rejection() {
    local expected=$1 result=0 output
    shift
    output=$(kctl "$@" 2>&1) || result=$?
    [[ "$result" != 0 ]] && rg -q -- "$expected" <<< "$output" || fail "Expected $expected rejection, got exit $result: $output"
}
create_probe() {
    local name=$1 value=$2 hold=$3
    kctl -n haso-p0 create -f - <<EOF
apiVersion: spikes.ha-operator.io/v1alpha1
kind: FrameworkProbe
metadata: {name: $name}
spec: {value: '$value', hold: $hold}
EOF
}
expect_rejection Forbidden get "$probe" -n default --as="$identity"
expect_rejection 'must be named framework' -n haso-p0 create -f - <<'EOF'
apiVersion: spikes.ha-operator.io/v1alpha1
kind: FrameworkProbe
metadata: {name: second}
spec: {value: invalid}
EOF
expect_rejection 'at least 1|too short|minimum' -n haso-p0 create -f - <<'EOF'
apiVersion: spikes.ha-operator.io/v1alpha1
kind: FrameworkProbe
metadata: {name: framework}
spec: {value: ''}
EOF

create_probe framework initial false
kctl -n haso-p0 wait --for=jsonpath='{.status.phase}'=Completed "$probe/framework" --timeout=60s
kctl -n haso-p0 wait --for=jsonpath='{.status.observedGeneration}'=1 "$probe/framework" --timeout=60s
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.value}') == initial ]] || fail 'Probe did not project real state.'
# The main resource endpoint cannot write status or change the observed generation.
kctl -n haso-p0 patch "$probe/framework" --type=merge -p '{"status":{"phase":"Accepted","observedGeneration":99}}'
[[ $(kctl -n haso-p0 get "$probe/framework" -o jsonpath='{.status.observedGeneration}:{.status.phase}') == 1:Completed ]] || fail 'Status subresource isolation failed.'

# Real API resourceVersion conflict, with the retained value checked afterward.
kctl -n haso-p0 get configmap/framework-probe-state -o json > "$DEV_STATE/framework-stale.json"
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"conflictMarker":"newer"}}'
expect_rejection 'Conflict|object has been modified' replace -f "$DEV_STATE/framework-stale.json"
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.conflictMarker}') == newer ]] || fail 'Stale write overwrote newer state.'
rm -- "$DEV_STATE/framework-stale.json"

# Persist an accepted intent, interrupt the combined process, and resume from the CR.
kctl -n haso-p0 patch "$probe/framework" --type=merge -p '{"spec":{"value":"after-restart","hold":true}}'
kctl -n haso-p0 wait --for=jsonpath='{.status.phase}'=Accepted "$probe/framework" --timeout=60s
kctl -n haso-p0 wait --for=jsonpath='{.status.observedGeneration}'=2 "$probe/framework" --timeout=60s
uid=$(kctl -n haso-p0 get "$probe/framework" -o jsonpath='{.metadata.uid}')
kctl -n haso-p0 rollout restart deployment/supervisor
kctl -n haso-p0 rollout status deployment/supervisor --timeout=120s
[[ $(kctl -n haso-p0 get "$probe/framework" -o jsonpath='{.metadata.uid}:{.status.phase}') == "$uid:Accepted" ]] || fail 'Accepted intent did not survive process interruption.'
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.value}') == initial ]] || fail 'Held intent performed a side effect.'
kctl -n haso-p0 exec -i core-0 -c core -- python - <<'PY'
import os, urllib.request
request = urllib.request.Request('http://supervisor/info', headers={'X-Supervisor-Token': os.environ['SUPERVISOR_TOKEN']})
with urllib.request.urlopen(request, timeout=10) as response:
    assert response.status == 200
print('Combined Supervisor API remains reachable after framework restart.')
PY
kctl -n haso-p0 patch "$probe/framework" --type=merge -p '{"spec":{"hold":false}}'
kctl -n haso-p0 wait --for=jsonpath='{.status.observedGeneration}'=3 "$probe/framework" --timeout=60s
kctl -n haso-p0 wait --for=jsonpath='{.status.phase}'=Completed "$probe/framework" --timeout=60s
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.value}') == after-restart ]] || fail 'Intent did not resume after restart.'
# A periodic reconcile repairs drift in its owned projection.
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"value":"drift"}}'
kctl -n haso-p0 wait --for=jsonpath='{.data.value}'=after-restart configmap/framework-probe-state --timeout=30s

# Failed cleanup must retain the finalizer, including across operator restart.
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"cleanupBlocked":"true"}}'
kctl -n haso-p0 delete "$probe/framework" --wait=false
kctl -n haso-p0 wait --for=jsonpath='{.metadata.deletionTimestamp}' "$probe/framework" --timeout=30s
sleep 3
kctl -n haso-p0 get "$probe/framework" -o jsonpath='{.metadata.finalizers}' | rg -q 'spikes.ha-operator.io/probefinalizer'
kctl -n haso-p0 rollout restart deployment/supervisor
kctl -n haso-p0 rollout status deployment/supervisor --timeout=120s
kctl -n haso-p0 get "$probe/framework" -o jsonpath='{.metadata.finalizers}' | rg -q 'spikes.ha-operator.io/probefinalizer'
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"cleanupBlocked":null}}'
kctl -n haso-p0 wait --for=delete "$probe/framework" --timeout=60s
[[ -z $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.ownerUid}{.data.value}') ]] || fail 'Finalizer did not clean up its owned projection.'
# A new resource UID must neither overwrite nor finalize another owner's data.
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"ownerUid":"different-owner","value":"retained"}}'
create_probe framework replacement false
kctl -n haso-p0 wait --for=jsonpath='{.status.phase}'=Accepted "$probe/framework" --timeout=60s
sleep 3
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.ownerUid}:{.data.value}') == different-owner:retained ]] || fail 'Reconciler overwrote another owner.'
kctl -n haso-p0 delete "$probe/framework" --timeout=60s
[[ $(kctl -n haso-p0 get configmap/framework-probe-state -o jsonpath='{.data.ownerUid}:{.data.value}') == different-owner:retained ]] || fail 'Finalizer cleaned another owner.'
kctl -n haso-p0 patch configmap/framework-probe-state --type=merge -p '{"data":{"ownerUid":null,"value":null,"conflictMarker":null}}'
printf '%s\n' 'P1 framework namespace/RBAC, schema/status, conflict, interruption/recovery and finalizer retry gates passed.'
