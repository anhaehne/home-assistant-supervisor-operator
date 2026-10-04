#!/usr/bin/env bash
# Install the actual P1 chart into the sealed project cluster.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
ns=haso-p1
kctl create namespace "$ns"
kctl -n "$ns" create secret generic credentials --from-file="core-token=$DEV_STATE/core-token" --from-file="gateway-token=$DEV_STATE/gateway-token"
hctl install haso "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n "$ns" \
    --set operatorImage=haso/operator:p0 --set gatewayImage=haso/gateway:p0 \
    --set "operatorBuildId=$(local_tool docker --host "$DEV_ENDPOINT" image inspect haso/operator:p0 --format '{{.Id}}')" \
    --set imagePullPolicy=Never --set coreImage=ghcr.io/home-assistant/home-assistant:2026.9.4 \
    --set "selectedNode=$(guard core-node "$DEV_IDENTITY")" --set "operatorNode=$(cluster_name)-control-plane" \
    --set 'operatorTolerations[0].key=node-role.kubernetes.io/control-plane' \
    --set 'operatorTolerations[0].operator=Exists' --set 'operatorTolerations[0].effect=NoSchedule' --wait --timeout 5m
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].reason}'=Succeeded homeassistantinstance/home-assistant --timeout=600s
printf '%s\n' 'P1 Helm installation and application health passed.'
