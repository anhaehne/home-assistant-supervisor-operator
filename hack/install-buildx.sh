#!/usr/bin/env bash
set -euo pipefail
umask 077
root=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
case $(uname -m) in
    x86_64) architecture=amd64; checksum=982ca20490b45ed1ec8d99795974d3d874a358f75938c9c237305010e6b7e548 ;;
    aarch64) architecture=arm64; checksum=efa38cb7aa7db2dbb9ad049b00b0a9737f66f033626177b5a4e845184ad7ab29 ;;
    *) printf '%s\n' 'Unsupported runner architecture for pinned Buildx.' >&2; exit 1 ;;
esac
plugin="$root/.dev-tools/docker-buildx"
mkdir -p "$root/.dev-tools"
if [[ -f "$plugin" ]] && printf '%s  %s\n' "$checksum" "$plugin" | sha256sum --check --status; then
    printf '%s\n' 'Pinned Docker Buildx 0.37.2 is already installed.'
    exit 0
fi
temporary=$(mktemp "$root/.dev-tools/buildx.XXXXXX")
trap 'rm -f -- "$temporary"' EXIT
curl --fail --location --silent --show-error --max-time 120 \
    "https://github.com/docker/buildx/releases/download/v0.37.2/buildx-v0.37.2.linux-$architecture" -o "$temporary"
printf '%s  %s\n' "$checksum" "$temporary" | sha256sum --check --status
chmod 700 "$temporary"
mv "$temporary" "$plugin"
printf '%s\n' 'Installed checksum-verified Docker Buildx 0.37.2 in the project tool cache.'
