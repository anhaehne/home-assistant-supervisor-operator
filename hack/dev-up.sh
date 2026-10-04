#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
[[ ! -e "$DEV_STATE" ]] || fail 'Local state already exists. Inspect it and run ./hack/dev-down.sh before creating a fresh cluster.'
check_network
mkdir -m 700 "$DEV_STATE"
if [[ -n "${HASO_SUITE_OWNER:-}" ]]; then
    printf '%s' "$HASO_SUITE_OWNER" > "$DEV_STATE/suite-owner"
fi
mkdir "$DEV_STATE/home" "$DEV_STATE/docker-config"
# This endpoint was provisioned and verified in the platform handover. Bind
# subsequent operations to this daemon, rather than trusting an ambient context.
guard init "$DEV_IDENTITY" "$(runtime_id)"
name=$(cluster_name)
printf 'Creating isolated cluster %s\n' "$name"
if ! local_tool kind create cluster --name "$name" --config "$DEV_ROOT/dev/kind.yaml" \
    --kubeconfig "$DEV_KUBECONFIG" --wait 180s --retain; then
    fail 'Cluster creation failed. State is retained for diagnosis; fix the setup, then use ./hack/dev-down.sh to remove only this cluster.'
fi
local_tool docker --host "$DEV_ENDPOINT" inspect "$name-control-plane" | guard seal-node "$DEV_IDENTITY"
local_tool docker --host "$DEV_ENDPOINT" inspect "$name-worker" | guard seal-worker "$DEV_IDENTITY"
config_json | guard seal-config "$DEV_IDENTITY" "$DEV_KUBECONFIG"
check_network
kctl wait --for=condition=Ready node --all --timeout=120s
printf 'Isolated cluster ready: %s\n' "$name"
