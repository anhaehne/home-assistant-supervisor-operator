#!/usr/bin/env bash
# Render only. The caller owns namespace/Secret creation and apply ordering.
set -euo pipefail
bundle=$(cd -- "$(dirname -- "$0")" && pwd -P)
namespace=${1:?Usage: render-manifests.sh NAMESPACE VALUES_FILE OUTPUT_DIRECTORY}
values=${2:?Provide installation values with selectedNode and storageClassName}
output=${3:?Provide output directory}
[[ $namespace =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ && ${#namespace} -le 63 ]] || { echo 'Invalid namespace.' >&2; exit 1; }
[[ -f $values ]] || { echo 'Installation values file does not exist.' >&2; exit 1; }
# An empty installation node is already rejected by the chart's required check.
# Refuse the example placeholders too, so the generated example cannot be applied accidentally.
if grep -q 'REPLACE_WITH_' "$values"; then echo 'Replace installation placeholders before rendering.' >&2; exit 1; fi
mkdir -p "$output"
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
helm template haso "$bundle/chart.tgz" --namespace "$namespace" --kube-version 1.37.0 --no-hooks \
    --values "$values" > "$temporary/manifests.yaml"
cp "$bundle/crds.yaml" "$output/crds.yaml"
mv "$temporary/manifests.yaml" "$output/manifests.yaml"
printf 'Rendered for namespace %s. Apply CRDs separately before manifests.\n' "$namespace"
