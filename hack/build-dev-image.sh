#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
[[ -x "$DEV_ROOT/.dev-tools/docker-buildx" ]] || fail 'Install the pinned builder using ./hack/install-buildx.sh.'
mkdir -p "$DEV_STATE/docker-config/cli-plugins"
if [[ ! -e "$DEV_STATE/docker-config/cli-plugins/docker-buildx" ]]; then
    ln -s "$DEV_ROOT/.dev-tools/docker-buildx" "$DEV_STATE/docker-config/cli-plugins/docker-buildx"
fi
# Building the tool image does not change this cluster. Release the cluster
# lock after identity checks so browser/API checks can proceed independently.
exec 9>&-
dotnet publish "$DEV_ROOT/tests/P0.E2E" --no-restore -c Release -o "$DEV_ROOT/dev/.smoke"
local_tool docker --host "$DEV_ENDPOINT" buildx build --load -f "$DEV_ROOT/dev/Dockerfile" -t haso/dev:p0 "$DEV_ROOT/dev"
local_tool docker --host "$DEV_ENDPOINT" run --rm --network none haso/dev:p0 bash -ec \
    'test "$(dotnet --version)" = 10.0.401; docker --version; docker buildx version; kind version; kubectl version --client; helm version --short; test -d /ms-playwright; rg --version; flock --version'
local_tool docker --host "$DEV_ENDPOINT" run --rm --network none --cap-drop ALL --security-opt no-new-privileges haso/dev:p0 \
    dotnet /opt/haso-browser-smoke/P0.E2E.dll browser-smoke
