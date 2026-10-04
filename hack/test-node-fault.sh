#!/usr/bin/env bash
# Fault injection ONLY into sealed nodes of the project-owned nested kind cluster.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
[[ $(guard has-worker "$DEV_IDENTITY") == true ]] || fail 'Node-partition acceptance requires a fresh guarded two-node cluster.'
artifacts=${1:?Provide the P1 artifact directory}
mkdir -p "$artifacts"
ns=haso-p1
worker="$(cluster_name)-worker"
control="$(cluster_name)-control-plane"
disconnected=false
stopped=false
control_cordoned=false
worker_cordoned=false
forward=''
request=''
cleanup() {
    result=$?
    trap - EXIT
    set +e
    # Release local clients and their inherited lock before any runtime guard
    # can fail or exit during a sidecar replacement.
    for process in "$request" "$forward"; do
        [[ -z "$process" ]] || { kill "$process" 2>/dev/null; wait "$process" 2>/dev/null; }
    done
    if [[ "$disconnected" == true || "$stopped" == true ]]; then
        (
            check_cluster || exit 1
            if [[ "$disconnected" == true ]]; then local_tool docker --host "$DEV_ENDPOINT" network connect kind "$worker" || exit 1; fi
            if [[ "$stopped" == true ]]; then local_tool docker --host "$DEV_ENDPOINT" start "$worker" >/dev/null || exit 1; fi
        ) || result=1
    fi
    if [[ "$control_cordoned" == true || "$worker_cordoned" == true ]]; then
        (
            check_cluster || exit 1
            if [[ "$control_cordoned" == true ]]; then kctl uncordon "$control" || exit 1; fi
            if [[ "$worker_cordoned" == true ]]; then kctl uncordon "$worker" || exit 1; fi
        ) || result=1
    fi
    exit "$result"
}
trap cleanup EXIT
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.spec.nodeName}') == "$worker" ]] || fail 'Core must be on the sealed worker for this test.'
# Allow Core onto the other sealed node only in this test fixture. Keep it
# cordoned while the template is replaced so the initial process stays on the
# worker. The chart itself supplies neither selectors nor tolerations for Core.
kctl cordon "$control"
control_cordoned=true
before_template=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
kctl -n "$ns" get configmap/core-workload -o jsonpath='{.data.core\.yaml}' > "$DEV_STATE/fault-core.yaml"
sed '/      automountServiceAccountToken:/a\      tolerations:\n        - {key: node-role.kubernetes.io/control-plane, operator: Exists, effect: NoSchedule}' "$DEV_STATE/fault-core.yaml" > "$DEV_STATE/fault-core-next.yaml"
kctl -n "$ns" create configmap core-workload --from-file="core.yaml=$DEV_STATE/fault-core-next.yaml" --dry-run=client -o yaml | kctl -n "$ns" apply -f -
for attempt in {1..180}; do
    prepared_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$prepared_uid" || "$prepared_uid" == "$before_template" ]] || break
    sleep 1
done
[[ -n "$prepared_uid" && "$prepared_uid" != "$before_template" ]] || fail 'Cross-node test toleration was not applied through an orderly template replacement.'
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
kctl -n "$ns" wait --for=jsonpath='{.status.podUid}'="$prepared_uid" homeassistantinstance/home-assistant --timeout=120s
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True homeassistantinstance/home-assistant --timeout=120s
kctl uncordon "$control"
control_cordoned=false
pvc_uid=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}')
pv_name=$(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.spec.volumeName}')
[[ -z $(kctl -n "$ns" get statefulset/core -o jsonpath='{.spec.template.spec.nodeSelector}') ]] || fail 'Core still has a node selector.'
operator_uid=$(kctl -n "$ns" get pods -l app=supervisor -o jsonpath='{.items[0].metadata.uid}')
# Disconnect the watch without restarting the operator process: stop only the
# nested cluster's static apiserver container, which its own kubelet restarts.
check_cluster
apiserver=$(local_tool docker --host "$DEV_ENDPOINT" exec "$control" crictl ps --name kube-apiserver -q)
[[ -n "$apiserver" && "$apiserver" != *$'\n'* ]] || fail 'Expected one project apiserver container.'
local_tool docker --host "$DEV_ENDPOINT" exec "$control" crictl stop "$apiserver" > "$artifacts/apiserver-stop.txt"
for attempt in {1..60}; do
    if kctl get --raw /readyz >/dev/null 2>&1; then break; fi
    sleep 1
done
kctl -n "$ns" patch homeassistantinstance/home-assistant --type=merge -p '{"spec":{"options":{"diagnostics":true}}}'
generation=$(kctl -n "$ns" get homeassistantinstance/home-assistant -o jsonpath='{.metadata.generation}')
kctl -n "$ns" wait --for=jsonpath='{.status.observedGeneration}'="$generation" homeassistantinstance/home-assistant --timeout=120s
[[ $(kctl -n "$ns" get pods -l app=supervisor -o jsonpath='{.items[0].metadata.uid}') == "$operator_uid" ]] || fail 'Watch recovery restarted the operator instead of reconnecting.'
kctl -n "$ns" logs deployment/supervisor --tail=500 | guard redact "$DEV_STATE" > "$artifacts/watch-recovery.log"
rg -q 'terminated and is reconnecting|Watch restarting' "$artifacts/watch-recovery.log" || fail 'No actual watch reconnection was observed.'

kctl_background "$artifacts/fault-api-forward.log" -n "$ns" port-forward --address 127.0.0.1 service/supervisor 18080:80
forward=$DEV_BACKGROUND_PID
for attempt in {1..40}; do
    curl --silent --fail --max-time 2 http://127.0.0.1:18080/health/live >/dev/null && break
    sleep 1
done
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
old_container=$(local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl ps --name '^core$' -q)
[[ -n "$old_container" && "$old_container" != *$'\n'* ]] || fail 'Expected one actual Core container before partition.'
check_cluster
local_tool docker --host "$DEV_ENDPOINT" network disconnect kind "$worker"
disconnected=true
curl --silent --show-error --fail-with-body --max-time 600 --config "$DEV_STATE/p1-curl.cfg" \
    -X POST -H 'Content-Type: application/json' -H 'Idempotency-Key: p1-partition-restart' -d '{}' \
    http://127.0.0.1:18080/core/restart > "$artifacts/partition-restart.json" 2> "$artifacts/partition-request.log" &
request=$!
kctl -n "$ns" wait --for=jsonpath='{.metadata.deletionTimestamp}' pod/core-0 --timeout=90s
sleep 10
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') == "$old_uid" ]] || fail 'An unfenced old Pod was replaced.'
[[ $(local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl ps --name '^core$' -q) == "$old_container" ]] || fail 'The live partitioned Core process was not retained as expected.'
[[ $(kctl -n "$ns" get statefulset/core -o jsonpath='{.spec.replicas}') == 0 ]] || fail 'A replacement was requested before termination.'
curl --silent --fail --config "$DEV_STATE/p1-curl.cfg" http://127.0.0.1:18080/jobs/info > "$artifacts/partition-jobs.json"
check_cluster
local_tool docker --host "$DEV_ENDPOINT" stop --time 30 "$worker" > "$artifacts/worker-fence.txt"
stopped=true
[[ $(local_tool docker --host "$DEV_ENDPOINT" inspect "$worker" --format '{{.State.Running}} {{.State.Pid}}') == 'false 0' ]] || fail 'Physical worker fencing was not established.'
# Let the old kubelet finish normal Pod termination, but prevent it from
# receiving the replacement. No force deletion or volume copying is involved.
kctl cordon "$worker"
worker_cordoned=true
check_cluster
local_tool docker --host "$DEV_ENDPOINT" network connect kind "$worker"
disconnected=false
local_tool docker --host "$DEV_ENDPOINT" start "$worker" > "$artifacts/worker-recovery.txt"
stopped=false
kctl wait --for=condition=Ready node/"$worker" --timeout=180s
# Kubernetes may still cache Ready=True from before the short partition. Wait
# for the restarted node's actual CRI before interpreting any process counts.
cri_ready=false
for attempt in {1..180}; do
    check_cluster
    if local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl info > "$artifacts/worker-cri-recovery.log" 2>&1; then
        cri_ready=true
        break
    fi
    sleep 1
done
[[ "$cri_ready" == true ]] || fail 'Restarted worker CRI did not become available.'
for attempt in {1..100}; do
    check_cluster
    worker_containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl ps --name '^core$' -q) || fail 'Failed to inspect worker Core processes.'
    control_containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$control" crictl ps --name '^core$' -q) || fail 'Failed to inspect control-plane Core processes.'
    containers=$(printf '%s\n' "$worker_containers" "$control_containers" | sed '/^$/d')
    [[ "$containers" != *$'\n'* ]] || fail 'Two actual Core containers overlapped during recovery.'
    new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
    sleep 1
done
[[ -n "$new_uid" && "$new_uid" != "$old_uid" ]] || fail 'Core did not recover after physical fencing.'
wait "$request"
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.spec.nodeName}') == "$control" ]] || fail 'Core did not recover on a different node.'
[[ -z $(local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl ps --name '^core$' -q) ]] || fail 'Old worker still has a running Core process.'
containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$control" crictl ps --name '^core$' -q)
[[ -n "$containers" && "$containers" != *$'\n'* ]] || fail 'Expected one replacement Core process on the other node.'
[[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.metadata.uid}/{.spec.volumeName}') == "$pvc_uid/$pv_name" ]] || fail 'Cross-node recovery replaced the PVC or PV.'
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Node fencing lost retained data.'
kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}/{.spec.nodeName}' > "$artifacts/cross-node-recovery.txt"
printf '%s\n' 'P1 real watch reconnect, live Core partition, refusal before physical fencing, and single-Core recovery on a different node with the same PVC/PV and retained data passed.'
