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
[[ -S /run/paseo-docker/docker.sock ]] || fail 'The provisioned dedicated Docker socket is missing.'
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
runtime_id() { local_tool docker --host "$DEV_ENDPOINT" info --format '{{.ID}}'; }
cluster_name() { guard name "$DEV_IDENTITY"; }
check_runtime() { guard runtime "$DEV_IDENTITY" "$(runtime_id)"; }
check_network() {
    local mtu kind_network
    mtu=$(cat /sys/class/net/eth0/mtu)
    local_tool docker --host "$DEV_ENDPOINT" network inspect bridge | guard network-mtu "$mtu"
    if kind_network=$(local_tool docker --host "$DEV_ENDPOINT" network inspect kind 2>/dev/null); then
        printf '%s' "$kind_network" | guard network-mtu "$mtu"
    fi
}
check_node() {
    local_tool docker --host "$DEV_ENDPOINT" inspect "$(cluster_name)-control-plane" |
        guard node "$DEV_IDENTITY"
}
config_json() {
    local_tool kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" config view --raw -o json
}
check_cluster() {
    [[ -f "$DEV_IDENTITY" && -f "$DEV_KUBECONFIG" ]] || fail 'No completed local cluster. Run ./hack/dev-up.sh.'
    check_runtime
    check_node
    config_json | guard config "$DEV_IDENTITY" "$DEV_KUBECONFIG"
}
kctl() {
    check_cluster
    local_tool kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" "$@"
}
# Launch the actual kubectl process, so callers can stop and wait for it without
# leaving an orphaned shell/function that keeps the cluster lock or port alive.
kctl_background() {
    local log_file=$1
    shift
    check_cluster
    env -i PATH="$PATH" HOME="$DEV_STATE/home" LANG=C.UTF-8 \
        kubectl --kubeconfig "$DEV_KUBECONFIG" --context "kind-$(cluster_name)" "$@" >"$log_file" 2>&1 &
    DEV_BACKGROUND_PID=$!
}
