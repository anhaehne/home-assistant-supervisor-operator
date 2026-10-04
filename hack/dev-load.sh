#!/usr/bin/env bash
source "$(dirname -- "$0")/dev-build.sh"
kctl create namespace haso-p0
od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$DEV_STATE/core-token"
od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$DEV_STATE/gateway-token"
kctl -n haso-p0 create secret generic credentials --from-file="core-token=$DEV_STATE/core-token" --from-file="gateway-token=$DEV_STATE/gateway-token"
kctl apply -f "$DEV_ROOT/deploy/framework-probe-crd.yaml"
kctl wait --for=condition=Established crd/frameworkprobes.spikes.ha-operator.io --timeout=60s
kctl apply -f "$DEV_ROOT/deploy/framework-probe.yaml"
kctl apply -f "$DEV_ROOT/deploy/p0.yaml"
kctl -n haso-p0 rollout status deployment/supervisor --timeout=120s
kctl -n haso-p0 wait --for=jsonpath='{.status.readyReplicas}'=1 statefulset/core --timeout=360s
printf '%s\n' 'P0 stock Core and the API/gateway deployment are ready.'
