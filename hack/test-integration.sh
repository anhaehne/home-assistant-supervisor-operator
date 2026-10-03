#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
kctl -n haso-p0 wait --for=condition=Ready pod -l app=supervisor --timeout=120s
identity=system:serviceaccount:haso-p0:supervisor
# Permissions stay bound to this installation's singleton Core exec endpoint.
kctl auth can-i create pods/core-0 --subresource=exec -n haso-p0 --as="$identity" | rg -qx yes
for check in 'create pods/other-pod --subresource=exec -n haso-p0' 'create pods/core-0 --subresource=exec -n default' 'get secrets -n haso-p0' 'create pods -n haso-p0'; do
    read -ra arguments <<< "$check"
    result=0
    answer=$(kctl auth can-i "${arguments[@]}" --as="$identity") || result=$?
    [[ "$result" == 1 && "$answer" == no ]] || fail "Expected an explicit RBAC denial, got exit $result / $answer: $check"
    printf 'RBAC denied: %s\n' "$check"
done
kctl -n haso-p0 exec -i core-0 -c core -- python - < "$DEV_ROOT/tests/P0.E2E/upstream_contracts.py"
printf '%s\n' 'P0 namespace/RBAC boundaries and real API/client integration passed.'
