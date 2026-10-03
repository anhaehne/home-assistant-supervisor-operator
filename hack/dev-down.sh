#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
[[ -f "$DEV_IDENTITY" ]] || fail 'No recorded project cluster; refusing cleanup.'
check_runtime
name=$(cluster_name)
# Inspect only the exact recorded name, including partially created clusters.
node=$(local_tool docker --host "$DEV_ENDPOINT" container ls -a --filter "name=^/${name}-control-plane$" --format '{{.ID}}')
if [[ -n "$node" ]]; then
    local_tool docker --host "$DEV_ENDPOINT" inspect "$name-control-plane" | guard seal-node "$DEV_IDENTITY"
    check_node
    volumes=$(local_tool docker --host "$DEV_ENDPOINT" inspect "$name-control-plane" --format '{{range .Mounts}}{{if eq .Type "volume"}}{{println .Name}}{{end}}{{end}}')
    printf '%s\n' "$volumes" > "$DEV_STATE/node-volumes"
    local_tool kind delete cluster --name "$name" --kubeconfig "$DEV_KUBECONFIG"
    remaining=$(local_tool docker --host "$DEV_ENDPOINT" container ls -a --filter "name=^/${name}-control-plane$" --format '{{.ID}}')
    [[ -z "$remaining" ]] || fail 'Recorded node still exists after cleanup; state retained.'
fi
if [[ -f "$DEV_STATE/node-volumes" ]]; then
    while read -r volume; do
        [[ -n "$volume" ]] || continue
        remaining_volumes=$(local_tool docker --host "$DEV_ENDPOINT" volume ls --filter "name=$volume" --format '{{.Name}}')
        if [[ -n "$remaining_volumes" ]]; then
            fail "Recorded node data volume remains after kind cleanup: $volume; state retained."
        fi
    done < "$DEV_STATE/node-volumes"
fi
rm -r -- "$DEV_STATE"
printf 'Removed recorded cluster and local state: %s\n' "$name"
