#!/usr/bin/env bash
# Tag publication creates a draft; acceptance and publication remain explicit.
set -euo pipefail
version=${1:?Usage: draft-release.sh VERSION ARTIFACT_DIRECTORY}
artifacts=${2:?Usage: draft-release.sh VERSION ARTIFACT_DIRECTORY}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || exit 1
: "${GH_REPO:?Set GH_REPO to OWNER/REPOSITORY}"
tag="v$version"
(cd "$artifacts" && sha256sum --check SHA256SUMS)
notes=$(mktemp)
trap 'rm -f "$notes"' EXIT
repository=${GH_REPO,,}
cat > "$notes" <<NOTES
P1 development preview: Core lifecycle only. Add-ons, backups and updates remain later milestones. Validated development target: Kubernetes 1.37.0, linux/amd64.

## Installation

Configure a storage class and an existing namespace-local credentials Secret using the included installation-values.example.yaml. See the artifact guide in the manifest bundle for credential keys and storage requirements.

\`\`\`sh
helm install haso oci://ghcr.io/$repository/charts/home-assistant-supervisor-operator --version $version --namespace home-assistant --create-namespace --values installation-values.yaml
\`\`\`

The chart uses immutable operator/gateway image digests listed in images.yaml. For kubectl or GitOps, unpack the manifest bundle, configure installation values, and run its render-manifests.sh helper. Verify downloaded assets with SHA256SUMS.

## Changes

Core no longer requires a selected node. Kubernetes schedules Core according to PVC topology. Recovery on another node requires accessible storage and verified termination of the old process. The isolated suite covers legacy selector removal and cross-node recovery with the same PVC/PV and retained configuration using shared test storage; production CSI behavior remains unvalidated.

## Acceptance before publication

This draft is pending anonymous chart/image download and a fresh installation check of the published artifacts. Repository and all three GHCR packages must be public for anonymous installation. Publish only after those checks pass. The full isolated P1 acceptance suite must also have passed for the application source in this tag.
NOTES
# Query all releases so authorization/network errors cannot masquerade as absence.
existing=$(gh api --paginate "repos/$GH_REPO/releases" --jq ".[] | select(.tag_name == \"$tag\") | .draft")
if [[ -n "$existing" ]]; then
    [[ "$existing" == true ]] || { echo 'Refusing to modify a published release.' >&2; exit 1; }
else
    flags=(--draft --verify-tag --title "$tag" --notes-file "$notes")
    if [[ "$version" == *-* ]]; then flags+=(--prerelease); fi
    gh release create "$tag" "${flags[@]}"
fi
gh release upload "$tag" --clobber \
    "$artifacts/home-assistant-supervisor-operator-$version.tgz" \
    "$artifacts/home-assistant-supervisor-operator-$version-manifests.tar.gz" \
    "$artifacts/images.yaml" "$artifacts/SHA256SUMS"
