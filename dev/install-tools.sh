#!/usr/bin/env bash
set -euo pipefail
[[ $(uname -m) == x86_64 ]] || { echo 'This initial validated development profile requires linux/amd64.' >&2; exit 1; }
temporary=$(mktemp -d)
trap 'rm -r -- "$temporary"' EXIT
printf 'Build network MTU: '
cat /sys/class/net/eth0/mtu
fetch() {
    local url=$1 checksum=$2 target=$3
    printf 'Downloading %s\n' "${url##*/}"
    curl --fail --location --show-error --verbose --retry 2 --connect-timeout 15 --max-time 180 "$url" -o "$target"
    printf '%s  %s\n' "$checksum" "$target" | sha256sum --check --status
}
fetch https://github.com/kubernetes-sigs/kind/releases/download/v0.33.0/kind-linux-amd64 aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d "$temporary/kind"
fetch https://dl.k8s.io/release/v1.37.0/bin/linux/amd64/kubectl 6129359f4e1f3848a5572ccb0b26cf28b8ca08cef38c95a765b2f64a2c961a2f "$temporary/kubectl"
fetch https://get.helm.sh/helm-v3.19.0-linux-amd64.tar.gz a7f81ce08007091b86d8bd696eb4d86b8d0f2e1b9f6c714be62f82f96a594496 "$temporary/helm.tar.gz"
tar -xzf "$temporary/helm.tar.gz" -C "$temporary" linux-amd64/helm
install -m 755 "$temporary/kind" "$temporary/kubectl" "$temporary/linux-amd64/helm" /usr/local/bin/
fetch https://download.docker.com/linux/static/stable/x86_64/docker-29.8.1.tgz d8db66739d2e28d4933786d73e918d9be643a67fbd835db1bf740d650a259e70 "$temporary/docker.tar.gz"
tar -xzf "$temporary/docker.tar.gz" -C "$temporary" docker/docker
install -m 755 "$temporary/docker/docker" /usr/local/bin/docker
fetch https://github.com/docker/buildx/releases/download/v0.37.2/buildx-v0.37.2.linux-amd64 982ca20490b45ed1ec8d99795974d3d874a358f75938c9c237305010e6b7e548 "$temporary/docker-buildx"
mkdir -p /usr/local/lib/docker/cli-plugins
install -m 755 "$temporary/docker-buildx" /usr/local/lib/docker/cli-plugins/docker-buildx
