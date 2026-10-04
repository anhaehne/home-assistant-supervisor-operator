#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
artifacts=${1:-"$DEV_ROOT/.test-artifacts/p1-interactive"}
mkdir -p "$artifacts"
ns=haso-p1
request_key="p1-interrupted-start-$(cat /proc/sys/kernel/random/uuid)"
instance=homeassistantinstance/home-assistant
operator_forward=''
core_forward=''
cleanup() {
    [[ -z "$operator_forward" ]] || { kill "$operator_forward" 2>/dev/null || true; wait "$operator_forward" 2>/dev/null || true; }
    [[ -z "$core_forward" ]] || { kill "$core_forward" 2>/dev/null || true; wait "$core_forward" 2>/dev/null || true; }
}
trap cleanup EXIT
forward_operator() {
    if [[ -n "$operator_forward" ]]; then kill "$operator_forward" 2>/dev/null || true; wait "$operator_forward" 2>/dev/null || true; fi
    kctl_background "$artifacts/operator-forward.log" -n "$ns" port-forward --address 127.0.0.1 service/supervisor 18080:80
    operator_forward=$DEV_BACKGROUND_PID
    for attempt in {1..40}; do
        if curl --silent --fail --max-time 2 http://127.0.0.1:18080/health/live >/dev/null; then return; fi
        sleep 1
    done
    fail 'Installed API forwarding failed.'
}
forward_operator
printf 'header = "X-Supervisor-Token: %s"\n' "$(cat "$DEV_STATE/core-token")" > "$DEV_STATE/p1-curl.cfg"
api() { curl --silent --show-error --fail-with-body --max-time 600 --config "$DEV_STATE/p1-curl.cfg" "http://127.0.0.1:18080$1" "${@:2}"; }
healthy() {
    local generation
    generation=$(kctl -n "$ns" get "$instance" -o jsonpath='{.metadata.generation}')
    kctl -n "$ns" wait --for=jsonpath='{.status.observedGeneration}'="$generation" "$instance" --timeout=600s
    kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].status}'=True "$instance" --timeout=600s
}
reject() {
    local output result=0
    output=$(kctl "$@" 2>&1) || result=$?
    [[ "$result" != 0 ]] && rg -q 'Forbidden|singleton|designated|immutable' <<< "$output" || fail "Expected admission/RBAC rejection: $output"
}
healthy
reject -n "$ns" create -f - <<'EOF'
apiVersion: ha-operator.io/v1alpha1
kind: HomeAssistantInstance
metadata: {name: second}
spec: {}
EOF
reject -n default create -f - <<'EOF'
apiVersion: ha-operator.io/v1alpha1
kind: HomeAssistantInstance
metadata: {name: home-assistant}
spec: {}
EOF
reject -n "$ns" scale deployment/supervisor --replicas=2
reject -n "$ns" scale statefulset/core --replicas=2
reject -n default get homeassistantinstances --as="system:serviceaccount:$ns:supervisor"
reject -n "$ns" get secrets --as="system:serviceaccount:$ns:supervisor"
result=0
hctl install second "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n default \
    --set operatorImage=haso/operator:p0 --set gatewayImage=haso/gateway:p0 > "$artifacts/second-install.log" 2>&1 || result=$?
[[ "$result" != 0 ]] && rg -q 'ownership|already exists' "$artifacts/second-install.log" || fail 'Second Helm installation was not rejected.'

kctl -n "$ns" exec core-0 -c core -- sh -ec 'printf retained > /config/p1-retained.txt'
api /core/stop -X POST -H 'Content-Type: application/json' -d '{}' > "$artifacts/stop.json"
kctl -n "$ns" wait --for=delete pod/core-0 --timeout=120s
api /core/info > "$artifacts/stopped-info.json"
api /network/info > "$artifacts/stopped-network.json"
api /jobs/info > "$artifacts/jobs-stopped.json"
# Accept a slow real Core start, interrupt the combined API/controller process,
# and verify the same durable command completes after the process returns.
api /core/start -X POST -H 'Content-Type: application/json' -H "Idempotency-Key: $request_key" -d '{}' > "$artifacts/interrupted-response.json" 2> "$artifacts/interrupted-request.log" &
request_pid=$!
for attempt in {1..40}; do
    operation=$(kctl -n "$ns" get "$instance" -o jsonpath='{.spec.command.id}')
    phase=$(kctl -n "$ns" get homeassistantoperation/"$operation" -o jsonpath='{.status.phase}' 2>/dev/null || true)
    [[ "$phase" != WaitingForHealth ]] || break
    sleep 1
done
[[ "$phase" == WaitingForHealth ]] || fail 'Start operation did not enter the real application health wait.'
kctl -n "$ns" rollout restart deployment/supervisor
kctl -n "$ns" rollout status deployment/supervisor --timeout=120s
wait "$request_pid" || true # The transport may close; the persisted operation is the assertion.
forward_operator
api /core/start -X POST -H 'Content-Type: application/json' -H "Idempotency-Key: $request_key" -d '{}' > "$artifacts/start-retry.json"
[[ $(kctl -n "$ns" get "$instance" -o jsonpath='{.spec.command.id}') == "$operation" ]] || fail 'Retry created a second lifecycle operation.'
healthy
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Core configuration was not retained.'
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
kctl -n "$ns" delete pod/core-0 --wait=true --timeout=120s
for attempt in {1..40}; do
    new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
    sleep 1
done
kctl -n "$ns" wait --for=condition=Ready pod/core-0 --timeout=600s
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') != "$old_uid" ]] || fail 'Deleted Core Pod did not recover.'
api /core/logs > "$artifacts/core.log"
api /supervisor/logs > "$artifacts/supervisor.log"
kctl -n "$ns" exec -i core-0 -c core -- python - < "$DEV_ROOT/tests/P0.E2E/upstream_contracts.py"

# Exercise the released frontend and Core's native restart service with an owner account.
dotnet build "$DEV_ROOT/tests/P0.E2E" --no-restore 9>&-
kctl_background "$artifacts/core-forward.log" -n "$ns" port-forward --address 127.0.0.1 service/core 18123:80
core_forward=$DEV_BACKGROUND_PID
for attempt in {1..40}; do
    curl --silent --fail --max-time 2 http://127.0.0.1:18123/ >/dev/null && break
    sleep 1
done
old_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
dotnet "$DEV_ROOT/tests/P0.E2E/bin/Debug/net10.0/P0.E2E.dll" fresh "$DEV_STATE" "$artifacts" lifecycle 2>&1 | guard redact "$DEV_STATE"
kill "$core_forward" 2>/dev/null || true
wait "$core_forward" 2>/dev/null || true
core_forward=''
for attempt in {1..80}; do
    new_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}' 2>/dev/null || true)
    [[ -z "$new_uid" || "$new_uid" == "$old_uid" ]] || break
    sleep 1
done
[[ -n "$new_uid" && "$new_uid" != "$old_uid" ]] || fail 'The native UI restart did not replace Core.'
healthy
gitops_uid=$(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}')
kctl -n "$ns" patch "$instance" --type=merge -p '{"spec":{"ownership":"GitOps"}}'
result=0
api /core/restart -X POST -H 'Content-Type: application/json' -d '{}' > "$artifacts/gitops-rejection.json" || result=$?
[[ "$result" == 22 ]] && rg -q GitOps "$artifacts/gitops-rejection.json" || fail 'UI ownership rejection failed.'
healthy
kctl -n "$ns" patch "$instance" --type=merge -p '{"spec":{"options":{"diagnostics":true}}}'
healthy
[[ $(kctl -n "$ns" get pod/core-0 -o jsonpath='{.metadata.uid}') == "$gitops_uid" ]] || fail 'GitOps replayed an old UI restart command.'
kctl -n "$ns" patch "$instance" --type=merge -p '{"spec":{"ownership":"Ui"}}'
healthy
# Shutdown runs before the API Deployment is removed; data is kept independently.
hctl uninstall haso -n "$ns" --wait --timeout 10m > "$artifacts/uninstall.log"
kctl -n "$ns" wait --for=delete pod/core-0 --timeout=120s
[[ $(kctl -n "$ns" get pvc/instance-data -o jsonpath='{.status.phase}') == Bound ]] || fail 'Helm uninstall did not retain the data PVC.'
hctl install haso "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n "$ns" \
    --set operatorImage=haso/operator:p0 --set gatewayImage=haso/gateway:p0 --set imagePullPolicy=Never \
    --set coreImage=ghcr.io/home-assistant/home-assistant:2026.9.4 \
    --set storageClassName=haso-test-shared --set "operatorNode=$(cluster_name)-control-plane" \
    --set 'operatorTolerations[0].key=node-role.kubernetes.io/control-plane' \
    --set 'operatorTolerations[0].operator=Exists' --set 'operatorTolerations[0].effect=NoSchedule' --wait --timeout 5m > "$artifacts/reinstall.log"
healthy
[[ $(kctl -n "$ns" exec core-0 -c core -- cat /config/p1-retained.txt) == retained ]] || fail 'Reinstallation lost Core data.'
printf '%s\n' 'P1 clean Helm install, singleton/RBAC, stop/start, interrupted operation/retry, Pod recovery, native UI restart, GitOps and retained uninstall/reinstall gates passed.'
