#!/usr/bin/env bash
# Package locally; this helper never contacts a Kubernetes API or registry.
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
version=${1:?Usage: package-deployment.sh VERSION OUTPUT OPERATOR_IMAGE GATEWAY_IMAGE}
output=${2:?Provide an output directory}
operator_image=${3:?Provide an operator image reference}
gateway_image=${4:?Provide a gateway image reference}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo 'Expected a SemVer version without build metadata.' >&2; exit 1; }
for image in "$operator_image" "$gateway_image"; do
    [[ $image =~ ^[a-z0-9][a-z0-9./:@_-]+$ && $image == *:* ]] || { echo 'Expected an explicit container tag or digest.' >&2; exit 1; }
done
mkdir -p "$output"
output=$(cd -- "$output" && pwd -P)
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
cp -R "$root/charts/home-assistant-supervisor-operator" "$temporary/chart"
sed -i "s|^operatorImage:.*|operatorImage: '$operator_image'|; s|^gatewayImage:.*|gatewayImage: '$gateway_image'|" "$temporary/chart/values.yaml"
helm lint "$temporary/chart" --strict --kube-version 1.37.0 --set-string selectedNode=example-linux-node
helm package "$temporary/chart" --version "$version" --app-version "$version" --destination "$output"
chart="home-assistant-supervisor-operator-$version.tgz"
bundle="$temporary/bundle"
mkdir "$bundle"
cp "$output/$chart" "$bundle/chart.tgz"
cp "$root/packaging/render-manifests.sh" "$bundle/render-manifests.sh"
cp "$root/packaging/installation-values.example.yaml" "$bundle/installation-values.example.yaml"
cp "$root/packaging/README.md" "$bundle/README.md"
helm show crds "$output/$chart" > "$bundle/crds.yaml"
helm template haso "$output/$chart" --namespace home-assistant --kube-version 1.37.0 --no-hooks \
    --set-string selectedNode=REPLACE_WITH_LINUX_NODE --set-string storageClassName=REPLACE_WITH_STORAGE_CLASS > "$bundle/manifests.example.yaml"
printf 'operatorImage: %s\ngatewayImage: %s\nversion: %s\nplatform: linux/amd64\n' "$operator_image" "$gateway_image" "$version" > "$output/images.yaml"
(cd "$bundle" && sha256sum chart.tgz crds.yaml manifests.example.yaml render-manifests.sh installation-values.example.yaml README.md > SHA256SUMS)
tar -czf "$output/home-assistant-supervisor-operator-$version-manifests.tar.gz" -C "$bundle" .
printf 'Packaged Helm chart and manifest bundle: %s\n' "$output"
