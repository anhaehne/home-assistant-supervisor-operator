#!/usr/bin/env bash
# Schema defaults and safe replacement of immutable workload fields.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:?Provide the P1 artifact directory}
ns=haso-p1
instance=homeassistantinstance/home-assistant
mkdir -p "$artifacts"
# Model the prior alpha controller's hash and injected selector. The current
# controller must retire that workload gracefully and retain the existing PVC.
legacy_node=$(guard core-node "$DEV_IDENTITY")
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
pvc_uid=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}')
# Preserve the trailing newline from the ConfigMap (command substitution trims
# it), so this is the old controller's exact hash input rather than a sentinel.
kctl -n "$ns" get configmap/core-workload -o jsonpath='{.data.core\.yaml}' > "$DEV_STATE/legacy-core.yaml"
legacy_hash=$({ cat "$DEV_STATE/legacy-core.yaml"; printf '%s' "$legacy_node"; } | sha256sum | cut -d ' ' -f 1 | tr 'a-f' 'A-F')
kctl -n "$ns" patch statefulset/core --type=merge -p "{\"metadata\":{\"annotations\":{\"ha-operator.io/template-hash\":\"$legacy_hash\"}},\"spec\":{\"template\":{\"spec\":{\"nodeSelector\":{\"kubernetes.io/hostname\":\"$legacy_node\"}}}}}"
for attempt in {1..180}; do
    check_cluster
    worker_containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$(cluster_name)-worker" crictl ps --name '^core$' -q)
    control_containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$(cluster_name)-control-plane" crictl ps --name '^core$' -q)
    containers=$(printf '%s\n' "$worker_containers" "$control_containers" | sed '/^$/d')
    [[ "$containers" != *$'\n'* ]] || fail 'Core processes overlapped while removing legacy pinning.'
    new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
    sleep 1
done
[[ -n "$new_uid" && "$new_uid" != "$old_uid" ]] || fail 'Legacy pinned workload was not replaced.'
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
kctl -n "$ns" wait --for=jsonpath='{.status.podUid}'="$new_uid" "$instance" --timeout=120s
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True "$instance" --timeout=120s
[[ -z $(kctl -n "$ns" get statefulset/core -o jsonpath='{.spec.template.spec.nodeSelector}') ]] || fail 'Upgrade retained legacy node pinning.'
[[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}') == "$pvc_uid" ]] || fail 'Removing pinning replaced the retained PVC.'
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Removing pinning lost configuration.'
forward=''
trap '[[ -z "$forward" ]] || { kill "$forward" 2>/dev/null || true; wait "$forward" 2>/dev/null || true; }' EXIT
# An early v1alpha1 object may omit all newly optional fields. Kubernetes
# defaults the persisted object; reconciliation observes the new generation.
kctl -n "$ns" patch "$instance" --type=merge -p '{"spec":{"options":{"diagnostics":true,"timezone":"Europe/London"}}}'
kctl -n "$ns" patch "$instance" --type=merge -p '{"spec":{"options":null,"ownership":null,"desiredState":null,"command":null}}'
[[ $(kctl -n "$ns" get "$instance" -o jsonpath='{.spec.ownership}/{.spec.desiredState}/{.spec.options.timezone}/{.spec.options.port}') == Ui/Running/UTC/80 ]] || fail 'Optional-field CRD migration defaults were not persisted.'
generation=$(kctl -n "$ns" get "$instance" -o jsonpath='{.metadata.generation}')
kctl -n "$ns" wait --for=jsonpath='{.status.observedGeneration}'="$generation" "$instance" --timeout=600s
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True "$instance" --timeout=600s
kctl_background "$artifacts/upgrade-api-forward.log" -n "$ns" port-forward --address 127.0.0.1 service/supervisor 18080:80
forward=$DEV_BACKGROUND_PID
for attempt in {1..40}; do
    curl --silent --fail --max-time 2 http://127.0.0.1:18080/health/live >/dev/null && break
    sleep 1
done
# Updates and restores remain M4 work. Rejection must leave the intent and
# actual Core process untouched; they cannot pretend to complete safely.
old_command=$(kctl -n "$ns" get "$instance" -o jsonpath='{.status.operationId}')
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
for route in /core/update /backups/invalid/restore/full; do
    result=0
    curl --silent --show-error --fail-with-body --config "$DEV_STATE/p1-curl.cfg" -X POST -H 'Content-Type: application/json' -d '{}' "http://127.0.0.1:18080$route" > "$artifacts/unsupported-$(basename "$route").json" || result=$?
    [[ "$result" == 22 ]] || fail "Unavailable mutation was accepted: $route"
done
[[ $(kctl -n "$ns" get "$instance" -o jsonpath='{.status.operationId}') == "$old_command" ]] || fail 'An unavailable mutation changed durable intent.'
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') == "$old_uid" ]] || fail 'An unavailable mutation replaced Core.'

old_workload=$(kctl -n "$ns" get statefulset/core -o jsonpath='{.metadata.uid}')
kctl -n "$ns" get configmap/core-workload -o jsonpath='{.data.core\.yaml}' > "$DEV_STATE/upgrade-core.yaml"
sed 's/serviceName: core$/serviceName: core-upgrade/' "$DEV_STATE/upgrade-core.yaml" > "$DEV_STATE/upgrade-core-next.yaml"
kctl -n "$ns" create configmap core-workload --from-file="core.yaml=$DEV_STATE/upgrade-core-next.yaml" --dry-run=client -o yaml | kctl -n "$ns" apply -f -
# Observe the actual nested runtime, not synthetic readiness. No two Core
# containers may coexist while the immutable StatefulSet is replaced.
node=$(guard core-node "$DEV_IDENTITY")
for attempt in {1..120}; do
    check_cluster
    containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$node" crictl ps --name '^core$' -q)
    [[ "$containers" != *$'\n'* ]] || fail 'Two Core processes overlapped during an immutable upgrade.'
    new_workload=$(kctl -n "$ns" get statefulset/core -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_workload" || "$new_workload" == "$old_workload" ]] || break
    sleep 1
done
[[ -n "$new_workload" && "$new_workload" != "$old_workload" ]] || fail 'Immutable StatefulSet was not safely recreated.'
[[ $(kctl -n "$ns" get statefulset/core -o jsonpath='{.spec.serviceName}') == core-upgrade ]] || fail 'Immutable field was not upgraded.'
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Immutable upgrade lost retained configuration.'
# Restore the chart-owned template and require a second orderly replacement.
kctl -n "$ns" create configmap core-workload --from-file="core.yaml=$DEV_STATE/upgrade-core.yaml" --dry-run=client -o yaml | kctl -n "$ns" apply -f -
for attempt in {1..120}; do
    service=$(kctl -n "$ns" get statefulset/core -o jsonpath='{.spec.serviceName}' 2>/dev/null || true)
    [[ "$service" != core ]] || break
    sleep 1
done
[[ "$service" == core ]] || fail 'Chart workload was not restored.'
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True "$instance" --timeout=600s
printf '%s\n' 'P1 removal of legacy pinning, optional-field defaults, immutable StatefulSet replacement without overlapping Core processes, retained data and unavailable update/restore rejection passed.'
