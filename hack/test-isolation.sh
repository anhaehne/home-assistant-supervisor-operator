#!/usr/bin/env bash
# Verify poisoned inputs fail before runtime/Kubernetes tools are invoked.
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
temporary=$(mktemp -d)
trap 'rm -r -- "$temporary"' EXIT
mkdir "$temporary/bin"
for tool in docker kind kubectl; do
    # Embed the local sentinel path: child environments intentionally do not
    # preserve ISOLATION_SENTINEL or any other inherited test variable.
    printf '#!/usr/bin/env bash\nprintf "%%s\\n" invoked >> %q\nexit 99\n' \
        "$temporary/contacted" > "$temporary/bin/$tool"
    chmod +x "$temporary/bin/$tool"
done
# Confirm the sentinel still works inside the same empty child environment used
# by project helpers, then clear the marker before exercising poison cases.
if env -i PATH="$temporary/bin:$PATH" docker >/dev/null 2>&1; then
    printf '%s\n' 'FAIL: sentinel unexpectedly succeeded.' >&2
    exit 1
fi
[[ -f "$temporary/contacted" ]] || { printf '%s\n' 'FAIL: sentinel did not record tool access.' >&2; exit 1; }
rm "$temporary/contacted"
for helper in dev-up.sh dev-down.sh dev-load.sh test-runner.sh test-integration.sh test-e2e.sh collect-diagnostics.sh refresh-contracts.sh build-dev-image.sh test-cluster.sh; do
    for poison in 'KUBECONFIG=/tmp/external-config' 'USE_EXISTING_CLUSTER=true' 'DOCKER_HOST=tcp://remote.invalid:2375'; do
        if env PATH="$temporary/bin:$PATH" "$poison" \
            "$root/hack/$helper" >"$temporary/result" 2>&1; then
            printf 'FAIL: %s accepted %s\n' "$helper" "$poison" >&2
            exit 1
        fi
        [[ ! -e "$temporary/contacted" ]] || { cat "$temporary/contacted" >&2; exit 1; }
        rg -q 'Development isolation check failed' "$temporary/result" || { cat "$temporary/result" >&2; exit 1; }
    done
done
# The outer Pod may supply service-discovery variables. Prove child processes
# receive none of them, without contacting any runtime or Kubernetes endpoint.
env KUBERNETES_SERVICE_HOST=192.0.2.1 KUBERNETES_SERVICE_PORT=443 \
    AWS_ACCESS_KEY_ID=dummy GOOGLE_APPLICATION_CREDENTIALS=/tmp/dummy \
    bash -c 'source "$1/hack/lib/dev.sh"; local_tool env' bash "$root" >"$temporary/environment"
if rg -q 'KUBERNETES_|AWS_|GOOGLE_|KUBECONFIG=' "$temporary/environment"; then
    printf '%s\n' 'FAIL: ambient credentials or discovery escaped sanitization.' >&2
    exit 1
fi
# Bash suppresses errexit transitively for conditional function calls. A failed
# prerequisite must still prevent later validation and API/runtime actions.
bash -s -- "$root" "$temporary" <<'SH'
source "$1/hack/lib/dev.sh"
DEV_IDENTITY="$2/mock-identity"
DEV_KUBECONFIG="$2/mock-kubeconfig"
touch "$DEV_IDENTITY" "$DEV_KUBECONFIG"
runtime_id() { printf '%s\n' daemon; }
local_tool() { printf '%s\n' '{}'; }
config_json() { printf '%s\n' '{}'; }
guard() {
    case "$1" in
        name) printf '%s\n' sealed ;;
        has-worker) printf '%s\n' true ;;
        node|worker|network-mtu) [[ "$1" != "$failed_guard" ]] ;;
        config) touch "$2.config-contacted" ;;
        runtime) [[ "$failed_guard" != network-mtu ]] ;;
        recover-runtime) touch "$2.rebound"; printf '%s\n' retained ;;
    esac
}
for failed_guard in node worker network-mtu; do
    if check_cluster; then
        printf 'FAIL: conditional cluster guard accepted failed %s validation.\n' "$failed_guard" >&2
        exit 1
    fi
    [[ ! -e "$DEV_IDENTITY.config-contacted" && ! -e "$DEV_IDENTITY.rebound" ]] || exit 1
    if kctl get pods; then
        printf '%s\n' 'FAIL: kubectl ignored the failed cluster guard.' >&2
        exit 1
    fi
done
SH
printf '%s\n' 'Isolation regression checks passed; poisoned inputs made no runtime or Kubernetes requests.'
