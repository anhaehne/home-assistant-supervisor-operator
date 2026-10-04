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
# A project-owned shared filesystem is mounted into both nested nodes. The P1
# static test PV uses it to exercise cross-node recovery without copying data.
# This is a test backend, not validation of a production CSI driver.
volume="$name-shared-data"
[[ -z $(local_tool docker --host "$DEV_ENDPOINT" volume ls --filter "name=^${volume}$" --format '{{.Name}}') ]] || fail 'Shared test volume already exists; refusing adoption.'
printf '%s' "$volume" > "$DEV_STATE/shared-volume"
local_tool docker --host "$DEV_ENDPOINT" volume create --label "ha-operator.io/test-cluster=$name" "$volume" >/dev/null
shared=$(shared_volume_path)
printf '%s' "$shared" > "$DEV_STATE/shared-path"
sed "/  - role:/a\    extraMounts:\n      - hostPath: $shared\n        containerPath: /haso-shared-data" "$DEV_ROOT/dev/kind.yaml" > "$DEV_STATE/kind.yaml"
printf 'Creating isolated cluster %s\n' "$name"
if ! local_tool kind create cluster --name "$name" --config "$DEV_STATE/kind.yaml" \
    --kubeconfig "$DEV_KUBECONFIG" --wait 180s --retain; then
    fail 'Cluster creation failed. State is retained for diagnosis; fix the setup, then use ./hack/dev-down.sh to remove only this cluster.'
fi
local_tool docker --host "$DEV_ENDPOINT" inspect "$name-control-plane" | guard seal-node "$DEV_IDENTITY"
local_tool docker --host "$DEV_ENDPOINT" inspect "$name-worker" | guard seal-worker "$DEV_IDENTITY"
config_json | guard seal-config "$DEV_IDENTITY" "$DEV_KUBECONFIG"
check_network
kctl wait --for=condition=Ready node --all --timeout=120s
printf 'Isolated cluster ready: %s\n' "$name"
