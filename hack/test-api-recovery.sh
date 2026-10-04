#!/usr/bin/env bash
# Force a framework-level GET outage while its list/watch stays authorized.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
artifacts=${1:?Provide the P1 artifact directory}
ns=haso-p1
restore=false
cleanup() {
    result=$?
    trap - EXIT
    if [[ "$restore" == true ]]; then kctl -n "$ns" apply -f "$DEV_STATE/recovery-role.yaml" || result=1; fi
    exit "$result"
}
trap cleanup EXIT
kctl -n "$ns" get role/supervisor -o yaml | sed '/^  resourceVersion:/d; /^  uid:/d; /^  creationTimestamp:/d' > "$DEV_STATE/recovery-role.yaml"
[[ $(kctl -n "$ns" get role/supervisor -o jsonpath='{.rules[0].resources[0]}/{.rules[0].verbs[0]}') == homeassistantinstances/get ]] || fail 'Unexpected Role structure for the fixed permission test.'
kctl -n "$ns" patch role/supervisor --type=json -p '[{"op":"remove","path":"/rules/0/verbs/0"}]'
restore=true
[[ $(kctl -n "$ns" auth can-i watch homeassistantinstances --as="system:serviceaccount:$ns:supervisor") == yes ]] || fail 'The test accidentally revoked watch.'
result=0
kctl -n "$ns" get homeassistantinstance/home-assistant --as="system:serviceaccount:$ns:supervisor" >/dev/null 2>&1 || result=$?
[[ "$result" != 0 ]] || fail 'The framework GET outage was not established.'
for attempt in {1..14}; do sleep 5; done
# Workload drift has no instance watch event. Recovery must rediscover it.
kctl -n "$ns" scale statefulset/core --replicas=0
kctl -n "$ns" wait --for=delete pod/core-0 --timeout=120s
kctl -n "$ns" apply -f "$DEV_STATE/recovery-role.yaml"
restore=false
for attempt in {1..60}; do
    pod_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$pod_uid" ]] || break
    sleep 1
done
[[ -n "$pod_uid" ]] || fail 'Restoring GET permission left framework reconciliation permanently dropped.'
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
kctl -n "$ns" wait --for=jsonpath='{.status.podUid}'="$pod_uid" homeassistantinstance/home-assistant --timeout=120s
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True homeassistantinstance/home-assistant --timeout=120s
kctl -n "$ns" logs deployment/supervisor --tail=500 | guard redact "$DEV_STATE" > "$artifacts/api-read-recovery.log"
printf '%s\n' 'P1 prolonged framework GET failure recovered without a watch event and repaired Core drift.'
