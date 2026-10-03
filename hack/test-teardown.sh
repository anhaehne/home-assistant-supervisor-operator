#!/usr/bin/env bash
# Regression against real cluster creation and automatic failure teardown.
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
result=0
HASO_TEST_FAIL_AFTER_SETUP=1 "$root/hack/test-cluster.sh" || result=$?
[[ "$result" == 41 ]] || { printf 'Expected deliberate failure 41, got %s; fix setup or inspect suite artifacts.\n' "$result" >&2; exit 1; }
[[ ! -e "$root/.dev-state" ]] || { echo 'Failure teardown left cluster state.' >&2; exit 1; }
printf '%s\n' 'Real injected-failure setup and automatic teardown passed.'
