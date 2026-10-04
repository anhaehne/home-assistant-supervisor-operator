#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
[[ -x "$DEV_ROOT/.dev-tools/docker-buildx" ]] || fail 'Docker Buildx is missing. Run ./hack/install-buildx.sh to install the pinned builder.'
mkdir -p "$DEV_STATE/docker-config/cli-plugins"
if [[ ! -e "$DEV_STATE/docker-config/cli-plugins/docker-buildx" ]]; then
    ln -s "$DEV_ROOT/.dev-tools/docker-buildx" "$DEV_STATE/docker-config/cli-plugins/docker-buildx"
fi
local_tool docker --host "$DEV_ENDPOINT" buildx version
core_image=ghcr.io/home-assistant/home-assistant:2026.9.4
core_digest=sha256:3e6710a7ab2a61311d9d899b719f6c3657791c63e8f4942cec4ebc42401d6b76
runtime_image=mcr.microsoft.com/dotnet/aspnet:10.0.12
runtime_digest=sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
# Refuse unexpected cached source images. Downloads always use pinned digests.
for entry in "$core_image@$core_digest" "$runtime_image@$runtime_digest"; do
    image=${entry%@*}
    digest=${entry##*@}
    if ! local_tool docker --host "$DEV_ENDPOINT" image inspect "$image" --format '{{join .RepoDigests " "}}' 2>/dev/null | rg -q -- "$digest"; then
        local_tool docker --host "$DEV_ENDPOINT" pull "$entry"
        local_tool docker --host "$DEV_ENDPOINT" tag "$entry" "$image"
    fi
done
# Persistent compiler/build workers must not inherit the parent's cluster lock.
dotnet publish "$DEV_ROOT/src/SupervisorOperator" --no-restore -c Release -o "$DEV_ROOT/.build/operator" 9>&-
dotnet publish "$DEV_ROOT/src/CoreGateway" --no-restore -c Release -o "$DEV_ROOT/.build/gateway" 9>&-
local_tool docker --host "$DEV_ENDPOINT" buildx build --load --network none --pull=false -f "$DEV_ROOT/dev/Operator.Dockerfile" -t haso/operator:p0 "$DEV_ROOT"
local_tool docker --host "$DEV_ENDPOINT" buildx build --load --network none --pull=false -f "$DEV_ROOT/dev/Gateway.Dockerfile" -t haso/gateway:p0 "$DEV_ROOT"
local_tool kind load docker-image haso/operator:p0 haso/gateway:p0 "$core_image" --name "$(cluster_name)"
