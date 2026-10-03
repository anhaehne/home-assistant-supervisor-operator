#!/usr/bin/env bash
# Infrastructure acceptance only; application integration is a separate gate.
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
check_network
busybox_digest=sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e
if ! local_tool docker --host "$DEV_ENDPOINT" image inspect busybox:1.37.0 --format '{{join .RepoDigests " "}}' 2>/dev/null | rg -q -- "$busybox_digest"; then
    local_tool docker --host "$DEV_ENDPOINT" pull "busybox:1.37.0@$busybox_digest"
    local_tool docker --host "$DEV_ENDPOINT" tag "busybox:1.37.0@$busybox_digest" busybox:1.37.0
fi
local_tool kind load docker-image busybox:1.37.0 --name "$(cluster_name)"
kctl create namespace runner-smoke
trap 'kctl delete namespace runner-smoke --wait=true --timeout=120s' EXIT
kctl apply -f "$DEV_ROOT/dev/runner-smoke.yaml"
kctl -n runner-smoke wait --for=condition=complete job/write --timeout=120s
kctl -n runner-smoke wait --for=jsonpath='{.status.phase}'=Bound pvc/data --timeout=60s
kctl -n runner-smoke apply -f "$DEV_ROOT/dev/runner-read.yaml"
kctl -n runner-smoke wait --for=condition=complete job/read --timeout=120s
printf 'Runner passed real scheduling, service DNS, PVC binding, cross-Pod persistence, and Job completion.\n'
