#!/usr/bin/env bash
set -euo pipefail
umask 077
# Reject poisoned ambient input before dependency downloads or orchestration
# locking; child helpers must not inherit the shared cluster lock.
source "$(dirname -- "$0")/lib/dev.sh"
exec 9>&-
root=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
exec 8>"$root/.dev-suite.lock"
flock -x 8
"$root/hack/recover-runtime.sh" 8>&-
# No child helper inherits the orchestration lock. Each independently guards
# its own runtime/cluster access and shares the separate development lock.
mkdir -p "$root/.test-artifacts"
artifacts=$(mktemp -d "$root/.test-artifacts/p0.XXXXXX")
owner=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
cleanup() {
    result=$?
    trap - EXIT INT TERM
    set +e
    if [[ -f "$root/.dev-state/suite-owner" ]] && [[ $(cat "$root/.dev-state/suite-owner") == "$owner" ]]; then
        if [[ -f "$root/.dev-state/identity.json" ]]; then
            "$root/hack/collect-diagnostics.sh" "$artifacts" 8>&- >"$artifacts/diagnostics-result.log" 2>&1
            "$root/hack/dev-down.sh" 8>&- >"$artifacts/teardown.log" 2>&1
            teardown_result=$?
            if [[ $teardown_result != 0 && $result == 0 ]]; then result=$teardown_result; fi
        else
            # Creation failed before any cluster name was recorded. Only the
            # state directory carrying this suite's private nonce is removed.
            rm -r -- "$root/.dev-state"
        fi
    fi
    if [[ -e "$root/.dev-state/suite-owner" ]] && [[ $(cat "$root/.dev-state/suite-owner") == "$owner" ]]; then
        printf '%s\n' 'Teardown failed; owned state retained for diagnosis.' >&2
        result=1
    fi
    printf '{"exit_code":%s,"teardown_complete":%s}\n' "$result" "$([[ ! -e "$root/.dev-state" ]] && echo true || echo false)" > "$artifacts/suite-result.json"
    printf 'P0/P1 suite exit code: %s. Artifacts: %s\n' "$result" "$artifacts"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
[[ ! -e "$root/.dev-state" ]] || { printf '%s\n' 'A local cluster/state already exists. Inspect and remove it with dev-down.sh before running a fresh suite.' >&2; exit 1; }
[[ -x "$root/.dev-tools/docker-buildx" ]] || { printf '%s\n' 'Missing pinned Buildx. Run ./hack/install-buildx.sh.' >&2; exit 1; }
dotnet restore "$root/HomeAssistantSupervisorOperator.slnx" --locked-mode 8>&- | tee "$artifacts/restore.log"
dotnet test "$root/HomeAssistantSupervisorOperator.slnx" --no-restore --logger 'trx;LogFileName=unit.trx' --results-directory "$artifacts/unit" 8>&- | tee "$artifacts/unit.log"
"$root/hack/test-isolation.sh" 8>&- | tee "$artifacts/isolation.log"
HASO_SUITE_OWNER="$owner" "$root/hack/dev-up.sh" 8>&-
if [[ ${HASO_TEST_FAIL_AFTER_SETUP:-0} == 1 ]]; then
    printf '%s\n' 'Intentional failure to verify automatic teardown.' >&2
    exit 41
fi
"$root/hack/dev-load.sh" 8>&-
"$root/hack/test-runner.sh" 8>&-
"$root/hack/refresh-contracts.sh" --check 8>&-
"$root/hack/test-integration.sh" 8>&- | tee "$artifacts/integration.log"
"$root/hack/test-framework.sh" 8>&- | tee "$artifacts/framework.log"
"$root/hack/test-e2e.sh" "$artifacts" 8>&- | tee "$artifacts/e2e.log"
"$root/hack/test-p1.sh" "$artifacts" 8>&- | tee "$artifacts/p1.log"
printf '%s\n' 'All P0/P1 suite gates passed.'
