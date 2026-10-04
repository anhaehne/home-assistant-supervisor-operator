#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
# The P0 compatibility fixture must be stopped before the production instance.
kctl -n haso-p0 scale statefulset/core --replicas=0
kctl -n haso-p0 wait --for=delete pod/core-0 --timeout=120s
artifacts=${1:?Provide the suite artifact directory}
exec 9>&-
"$DEV_ROOT/hack/dev-install.sh"
"$DEV_ROOT/hack/test-lifecycle.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-pod-network-proxy.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-ingress-proxy.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-upgrades.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-dependency-recovery.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-node-fault.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-api-recovery.sh" "$artifacts/p1"
"$DEV_ROOT/hack/test-ingress-proxy.sh" "$artifacts/p1" fresh
