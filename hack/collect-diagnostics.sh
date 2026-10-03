#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
artifacts=${1:?Provide an artifact directory}
mkdir -p "$artifacts"
# No kubeconfig, Secrets, Pod environment values, or browser storage are exported.
kctl -n haso-p0 get pods -o wide | guard redact "$DEV_STATE" > "$artifacts/pods.txt"
kctl -n haso-p0 get events --sort-by=.lastTimestamp | guard redact "$DEV_STATE" > "$artifacts/events.txt"
for entry in 'deployment/supervisor supervisor' 'core-0 core' 'core-0 gateway'; do
    read -r workload container <<< "$entry"
    kctl -n haso-p0 logs "$workload" -c "$container" --tail=500 | guard redact "$DEV_STATE" > "$artifacts/$container.log" || true
done
kctl get nodes -o custom-columns=NAME:.metadata.name,VERSION:.status.nodeInfo.kubeletVersion,OS:.status.nodeInfo.osImage > "$artifacts/nodes.txt"
guard name "$DEV_IDENTITY" > "$artifacts/cluster-name.txt"
local_tool docker --host "$DEV_ENDPOINT" image inspect haso/operator:p0 haso/gateway:p0 ghcr.io/home-assistant/home-assistant:2026.9.4 \
    --format '{{.Id}} {{join .RepoDigests " "}}' > "$artifacts/images.txt"
