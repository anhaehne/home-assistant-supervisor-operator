#!/usr/bin/env bash
# Sidecar restart recovery. Never contact an old API or adopt replacement nodes.
source "$(dirname -- "$0")/lib/dev.sh"
[[ -f "$DEV_IDENTITY" ]] || exit 0
name=$(cluster_name)
new_daemon=$(runtime_id)
check_network
if guard runtime "$DEV_IDENTITY" "$new_daemon" >/dev/null 2>&1; then exit 0; fi
mode=$(runtime_recovery "$new_daemon")
if [[ "$mode" == retained ]]; then
    check_cluster
    printf '%s\n' 'Dedicated sidecar restarted; sealed nodes and kubeconfig survived and were revalidated.'
elif [[ "$mode" == lost ]]; then
    mkdir -p "$DEV_ROOT/.test-artifacts"
    archive=$(mktemp -d "$DEV_ROOT/.test-artifacts/runtime-restart.XXXXXX")
    printf 'Cluster %s was lost when the dedicated sidecar restarted. Replacement daemon: %s\n' "$name" "$new_daemon" > "$archive/recovery.txt"
    # Preserve private credentials and identity with mode 0700. This is an
    # interrupted run, never a successful teardown or retained-data recovery.
    mv -- "$DEV_STATE" "$archive/private-state"
    printf 'Dedicated sidecar lost the test nodes. Archived interrupted state: %s\n' "$archive"
else
    fail 'Unexpected runtime recovery disposition.'
fi
