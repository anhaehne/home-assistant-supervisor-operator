#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
artifacts=${1:?Provide an artifact directory}
mkdir -p "$artifacts"
# No kubeconfig, Secrets, Pod environment values, or browser storage are exported.
kctl -n haso-p0 get pods -o wide | guard redact "$DEV_STATE" > "$artifacts/pods.txt"
kctl -n haso-p0 get events --sort-by=.lastTimestamp | guard redact "$DEV_STATE" > "$artifacts/events.txt"
# Framework metadata/status only; do not export arbitrary probe values or ConfigMap data.
if kctl get crd/frameworkprobes.spikes.ha-operator.io >/dev/null 2>&1; then
    kctl -n haso-p0 get frameworkprobes.spikes.ha-operator.io \
        -o custom-columns=NAME:.metadata.name,UID:.metadata.uid,GENERATION:.metadata.generation,OBSERVED:.status.observedGeneration,PHASE:.status.phase,DELETING:.metadata.deletionTimestamp,FINALIZERS:.metadata.finalizers \
        > "$artifacts/framework-status.txt"
fi
for entry in 'deployment/supervisor supervisor' 'core-0 core' 'core-0 gateway'; do
    read -r workload container <<< "$entry"
    kctl -n haso-p0 logs "$workload" -c "$container" --tail=500 | guard redact "$DEV_STATE" > "$artifacts/$container.log" || true
done
for diagnostic_namespace in haso-p1 haso-proxy-fresh; do
    diagnostic_prefix=${diagnostic_namespace#haso-}
if kctl get namespace/"$diagnostic_namespace" >/dev/null 2>&1; then
    kctl -n "$diagnostic_namespace" get pods -o wide | guard redact "$DEV_STATE" > "$artifacts/$diagnostic_prefix-pods.txt"
    kctl -n "$diagnostic_namespace" get events --sort-by=.lastTimestamp | guard redact "$DEV_STATE" > "$artifacts/$diagnostic_prefix-events.txt"
    kctl -n "$diagnostic_namespace" get homeassistantinstances.ha-operator.io \
        -o custom-columns=NAME:.metadata.name,UID:.metadata.uid,GENERATION:.metadata.generation,OBSERVED:.status.observedGeneration,STATE:.status.state,OPERATION:.status.operationId,CONDITIONS:.status.conditions,FINALIZERS:.metadata.finalizers \
        | guard redact "$DEV_STATE" > "$artifacts/$diagnostic_prefix-instance-status.txt" || true
    kctl -n "$diagnostic_namespace" get homeassistantoperations.ha-operator.io \
        -o custom-columns=NAME:.metadata.name,ACTION:.spec.action,PHASE:.status.phase,STARTED:.status.startedAt,COMPLETED:.status.completedAt,ERROR:.status.error \
        | guard redact "$DEV_STATE" > "$artifacts/$diagnostic_prefix-operation-status.txt" || true
    for entry in 'deployment/supervisor supervisor' 'core-0 core' 'core-0 gateway'; do
        read -r workload container <<< "$entry"
        kctl -n "$diagnostic_namespace" logs "$workload" -c "$container" --tail=500 | guard redact "$DEV_STATE" > "$artifacts/$diagnostic_prefix-$container.log" || true
    done
fi
done
kctl get nodes -o custom-columns=NAME:.metadata.name,VERSION:.status.nodeInfo.kubeletVersion,OS:.status.nodeInfo.osImage > "$artifacts/nodes.txt"
guard name "$DEV_IDENTITY" > "$artifacts/cluster-name.txt"
local_tool docker --host "$DEV_ENDPOINT" image inspect haso/operator:p0 haso/gateway:p0 ghcr.io/home-assistant/home-assistant:2026.9.4 \
    --format '{{.Id}} {{join .RepoDigests " "}}' > "$artifacts/images.txt"
