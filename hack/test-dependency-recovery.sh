#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:?Provide the P1 artifact directory}
ns=haso-p1
instance=homeassistantinstance/home-assistant
restore=false
cleanup() {
    result=$?
    trap - EXIT
    if [[ "$restore" == true ]]; then
        kctl -n "$ns" apply -f "$DEV_STATE/recovery-workload.yaml" || result=1
    fi
    exit "$result"
}
trap cleanup EXIT
# Keep the complete chart-owned template private, never in diagnostics.
kctl -n "$ns" get configmap/core-workload -o yaml | sed '/^  resourceVersion:/d; /^  uid:/d; /^  creationTimestamp:/d' > "$DEV_STATE/recovery-workload.yaml"
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
kctl -n "$ns" delete configmap/core-workload
restore=true
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].reason}'=ReconciliationBlocked "$instance" --timeout=30s
# Longer than the framework's default five exponential error retries. The
# controller must keep polling; ConfigMap restoration has no instance event.
for attempt in {1..14}; do sleep 5; done
kctl -n "$ns" apply -f "$DEV_STATE/recovery-workload.yaml"
restore=false
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True "$instance" --timeout=120s
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') == "$old_uid" ]] || fail 'Dependency recovery unnecessarily restarted Core.'
kctl -n "$ns" logs deployment/supervisor --tail=500 | guard redact "$DEV_STATE" > "$artifacts/dependency-recovery.log"
printf '%s\n' 'P1 dependency outage beyond the framework retry budget recovered without an instance event or Core restart.'
