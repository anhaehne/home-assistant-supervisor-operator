#!/usr/bin/env bash
# Install the actual P1 chart into the sealed project cluster.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
ns=haso-p1
kctl create namespace "$ns"
# Both exact sealed nodes must mount the same owned backing filesystem. A
# static RWO PVC has no node affinity; Kubernetes selects the replacement node.
shared=$(shared_volume_path)
for role in control-plane worker; do
    mount=$(local_tool docker --host "$DEV_ENDPOINT" inspect "$(cluster_name)-$role" --format '{{range .Mounts}}{{if eq .Destination "/haso-shared-data"}}{{.Source}}{{end}}{{end}}')
    [[ "$mount" == "$shared" ]] || fail 'Shared test filesystem is missing from a sealed node.'
done
kctl create -f - <<'EOF'
apiVersion: v1
kind: PersistentVolume
metadata: {name: haso-p1-shared}
spec:
  capacity: {storage: 1Gi}
  accessModes: [ReadWriteOnce]
  persistentVolumeReclaimPolicy: Retain
  storageClassName: haso-test-shared
  claimRef: {namespace: haso-p1, name: instance-data}
  hostPath: {path: /haso-shared-data, type: Directory}
EOF
kctl -n "$ns" create secret generic credentials --from-file="core-token=$DEV_STATE/core-token" --from-file="gateway-token=$DEV_STATE/gateway-token"
hctl install haso "$DEV_ROOT/charts/home-assistant-supervisor-operator" -n "$ns" \
    --set operatorImage=haso/operator:p0 --set gatewayImage=haso/gateway:p0 \
    --set "operatorBuildId=$(local_tool docker --host "$DEV_ENDPOINT" image inspect haso/operator:p0 --format '{{.Id}}')" \
    --set imagePullPolicy=Never --set coreImage=ghcr.io/home-assistant/home-assistant:2026.9.4 \
    --set storageClassName=haso-test-shared --set "operatorNode=$(cluster_name)-control-plane" \
    --set 'operatorTolerations[0].key=node-role.kubernetes.io/control-plane' \
    --set 'operatorTolerations[0].operator=Exists' --set 'operatorTolerations[0].effect=NoSchedule' --wait --timeout 5m
kctl -n "$ns" wait --for=jsonpath='{.status.conditions[0].reason}'=Succeeded homeassistantinstance/home-assistant --timeout=600s
printf '%s\n' 'P1 Helm installation and application health passed.'
