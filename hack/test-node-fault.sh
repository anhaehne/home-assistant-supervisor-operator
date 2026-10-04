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
    exit "$result"
}
trap cleanup EXIT
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.spec.nodeName}') == "$worker" ]] || fail 'Core must be on the sealed worker for this test.'
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
check_cluster
local_tool docker --host "$DEV_ENDPOINT" network connect kind "$worker"
disconnected=false
local_tool docker --host "$DEV_ENDPOINT" start "$worker" > "$artifacts/worker-recovery.txt"
stopped=false
kctl wait --for=condition=Ready node/"$worker" --timeout=180s
for attempt in {1..100}; do
    check_cluster
    containers=$(local_tool docker --host "$DEV_ENDPOINT" exec "$worker" crictl ps --name '^core$' -q)
    [[ "$containers" != *$'\n'* ]] || fail 'Two actual Core containers overlapped during recovery.'
    new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
    sleep 1
done
[[ -n "$new_uid" && "$new_uid" != "$old_uid" ]] || fail 'Core did not recover after physical fencing.'
wait "$request"
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Node fencing lost retained data.'
printf '%s\n' 'P1 real watch reconnect, live Core node partition, refusal to replace before physical fencing, and retained single-Core recovery passed.'
