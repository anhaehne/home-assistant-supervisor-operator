#!/usr/bin/env bash
# Shared guard; source from a project helper before invoking runtime/cluster tools.
set -euo pipefail
umask 077
DEV_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
DEV_STATE="$DEV_ROOT/.dev-state"
DEV_IDENTITY="$DEV_STATE/identity.json"
DEV_KUBECONFIG="$DEV_STATE/kubeconfig"
DEV_ENDPOINT=unix:///run/paseo-docker/docker.sock
DEV_GUARD="$DEV_ROOT/tools/DevGuard/bin/Debug/net10.0/DevGuard.dll"

fail() { printf '%s\n' "$*" >&2; exit 1; }
for tool in dotnet docker kind kubectl flock; do
    command -v "$tool" >/dev/null || fail "Missing $tool. Repair the runner before continuing."
done
if ! build_output=$(dotnet build "$DEV_ROOT/tools/DevGuard/DevGuard.csproj" --no-restore --verbosity quiet 2>&1); then
    printf '%s\n' "$build_output" >&2
    fail 'DevGuard build failed. Run dotnet restore and fix the reported build/setup issue.'
fi
guard() { dotnet "$DEV_GUARD" "$@"; }
guard preflight
for attempt in {1..60}; do
    [[ ! -S /run/paseo-docker/docker.sock ]] || break
    sleep 1
done
[[ -S /run/paseo-docker/docker.sock ]] || fail 'The provisioned dedicated Docker socket did not return within 60 seconds.'
[[ ! -L "$DEV_STATE" ]] || fail 'Test state must not be a symbolic link.'
exec 9>"$DEV_ROOT/.dev.lock"
flock -x 9

# Child tools receive no Kubernetes service discovery, cloud credentials, ambient
# kubeconfig, credential plugins, Docker contexts, or default user configuration.
local_tool() {
    env -i PATH="$PATH" HOME="$DEV_STATE/home" LANG=C.UTF-8 \
        DOCKER_HOST="$DEV_ENDPOINT" DOCKER_CONFIG="$DEV_STATE/docker-config" \
        KIND_EXPERIMENTAL_PROVIDER=docker "$@"
}
runtime_id() {
    local attempt identity
    for attempt in {1..60}; do
        if identity=$(local_tool docker --host "$DEV_ENDPOINT" info --format '{{.ID}}' 2>/dev/null) && [[ -n "$identity" ]]; then
            printf '%s\n' "$identity"
            return
        fi
        sleep 1
    done
    fail 'Dedicated Docker daemon did not return within 60 seconds; state retained.'
}
cluster_name() { guard name "$DEV_IDENTITY"; }
runtime_recovery() {
    local replacement=$1 ids inspection name role found sealed
    name=$(cluster_name) || return
    ids=$(local_tool docker --host "$DEV_ENDPOINT" container ls -aq --no-trunc --filter "label=io.x-k8s.kind.cluster=$name") || return
    # Labels alone cannot establish absence: inspect exact names and sealed
    # IDs as well, so relabelled/renamed survivors fail rather than being lost.
    for role in control-plane worker; do
        found=$(local_tool docker --host "$DEV_ENDPOINT" container ls -aq --no-trunc --filter "name=^/${name}-${role}$") || return
        ids=$(printf '%s\n' "$ids" "$found" | sed '/^$/d' | sort -u)
    done
    for command in node-id worker-id; do
        sealed=$(guard "$command" "$DEV_IDENTITY") || return
        [[ -n "$sealed" ]] || continue
        found=$(local_tool docker --host "$DEV_ENDPOINT" container ls -aq --no-trunc --filter "id=$sealed") || return
        ids=$(printf '%s\n' "$ids" "$found" | sed '/^$/d' | sort -u)
    done
    if [[ -n "$ids" ]]; then
        local -a node_ids
        readarray -t node_ids <<< "$ids"
        inspection=$(local_tool docker --host "$DEV_ENDPOINT" inspect "${node_ids[@]}") || return
    else
        inspection='[]'
    fi
    printf '%s' "$inspection" | guard recover-runtime "$DEV_IDENTITY" "$replacement"
}
check_runtime() {
    local current mode
    current=$(runtime_id) || return
    if guard runtime "$DEV_IDENTITY" "$current" >/dev/null 2>&1; then return; fi
    check_network || return
    mode=$(runtime_recovery "$current") || return
    [[ "$mode" == retained ]] || fail 'Dedicated sidecar lost the test nodes. Run recover-runtime.sh, then rerun the fresh suite; never reuse its old API.'
}
check_network() {
    local mtu kind_network
    runtime_id >/dev/null || return
    mtu=$(cat /sys/class/net/eth0/mtu) || return
    local_tool docker --host "$DEV_ENDPOINT" network inspect bridge | guard network-mtu "$mtu" || return
    if kind_network=$(local_tool docker --host "$DEV_ENDPOINT" network inspect kind 2>/dev/null); then
        printf '%s' "$kind_network" | guard network-mtu "$mtu" || return
    fi
}
check_node() {
    local name worker
    name=$(cluster_name) || return
    local_tool docker --host "$DEV_ENDPOINT" inspect "$name-control-plane" |
        guard node "$DEV_IDENTITY" || return
    worker=$(guard has-worker "$DEV_IDENTITY") || return
    if [[ "$worker" == true ]]; then
        local_tool docker --host "$DEV_ENDPOINT" inspect "$name-worker" | guard worker "$DEV_IDENTITY" || return
    fi
}
config_json() {
    local_tool kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" config view --raw -o json
}
check_cluster() {
    [[ -f "$DEV_IDENTITY" && -f "$DEV_KUBECONFIG" ]] || fail 'No completed local cluster. Run ./hack/dev-up.sh.'
    check_runtime || return
    check_node || return
    config_json | guard config "$DEV_IDENTITY" "$DEV_KUBECONFIG"
}
kctl() {
    check_cluster || return
    local_tool kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" "$@"
}
hctl() {
    command -v helm >/dev/null || fail 'Missing pinned Helm; repair the development toolchain.'
    check_cluster || return
    local_tool helm --kubeconfig "$DEV_KUBECONFIG" --kube-context "kind-$(cluster_name)" "$@"
}
# Launch the actual kubectl process, so callers can stop and wait for it without
# leaving an orphaned shell/function that keeps the cluster lock or port alive.
kctl_background() {
    local log_file=$1
    shift
    check_cluster || return
    env -i PATH="$PATH" HOME="$DEV_STATE/home" LANG=C.UTF-8 \
        kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" "$@" >"$log_file" 2>&1 &
    DEV_BACKGROUND_PID=$!
}
