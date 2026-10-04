#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
[[ -f "$DEV_IDENTITY" ]] || fail 'No recorded project cluster; refusing cleanup.'
check_runtime
name=$(cluster_name)
nodes_present=false
# Inspect only the two exact project names, including partial creation or an
# already stopped/fenced worker. Never enumerate or delete arbitrary clusters.
for role in control-plane worker; do
    node_name="$name-$role"
    node=$(local_tool docker --host "$DEV_ENDPOINT" container ls -a --filter "name=^/${node_name}$" --format '{{.ID}}')
    [[ -n "$node" ]] || continue
    if [[ "$role" == control-plane ]]; then command=seal-node; else command=seal-worker; fi
    local_tool docker --host "$DEV_ENDPOINT" inspect "$node_name" | guard "$command" "$DEV_IDENTITY"
    local_tool docker --host "$DEV_ENDPOINT" inspect "$node_name" --format '{{range .Mounts}}{{if eq .Type "volume"}}{{println .Name}}{{end}}{{end}}' >> "$DEV_STATE/node-volumes"
    nodes_present=true
done
if [[ "$nodes_present" == true ]]; then
    local_tool kind delete cluster --name "$name" --kubeconfig "$DEV_KUBECONFIG"
    for role in control-plane worker; do
        remaining=$(local_tool docker --host "$DEV_ENDPOINT" container ls -a --filter "name=^/${name}-${role}$" --format '{{.ID}}')
        [[ -z "$remaining" ]] || fail 'A recorded node remains after cleanup; state retained.'
    done
fi
if [[ -f "$DEV_STATE/node-volumes" ]]; then
    while read -r volume; do
        [[ -n "$volume" ]] || continue
        remaining_volumes=$(local_tool docker --host "$DEV_ENDPOINT" volume ls --filter "name=$volume" --format '{{.Name}}')
        [[ -z "$remaining_volumes" ]] || fail "Recorded node data volume remains after kind cleanup: $volume; state retained."
    done < "$DEV_STATE/node-volumes"
fi
rm -r -- "$DEV_STATE"
printf 'Removed recorded cluster and local state: %s\n' "$name"
