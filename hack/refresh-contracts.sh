#!/usr/bin/env bash
source "$(dirname -- "$0")/lib/dev.sh"
check_cluster
mode=${1:---check}
[[ "$mode" == --check || "$mode" == --write ]] || fail 'Use --check or --write.'
inputs="$DEV_ROOT/.dev-tools/contracts"
mkdir -p "$inputs/supervisor" "$inputs/core"
fetch() {
    local url=$1 checksum=$2 target=$3
    if [[ ! -f "$target" ]]; then
        local_tool curl --disable --fail --location --silent --show-error --connect-timeout 15 --max-time 120 "$url" -o "$target"
    fi
    printf '%s  %s\n' "$checksum" "$target" | sha256sum --check --status || fail "Pinned contract source checksum mismatch: $target"
}
fetch https://raw.githubusercontent.com/home-assistant/supervisor/2026.09.3/supervisor/api/__init__.py 112d0046007c7368fef18a6f0c218374c7ced89a002c570261831752acad65dc "$inputs/supervisor/__init__.py"
fetch https://raw.githubusercontent.com/home-assistant/core/2026.9.4/homeassistant/components/hassio/__init__.py b38b7964badb6f4df26682d25c0859074ebf47acdcb8750d940459a055acba84 "$inputs/core/__init__.py"
fetch https://raw.githubusercontent.com/home-assistant/core/2026.9.4/homeassistant/components/hassio/coordinator.py deb06b6b4186764405e05806266ceb1dcf67a82c3dd559ccda94df8f2c3ac2ec "$inputs/core/coordinator.py"
output="$DEV_STATE/baseline.generated.json"
tar -C "$inputs" -cf - supervisor/__init__.py core/__init__.py core/coordinator.py | \
    kctl -n haso-p0 exec -i core-0 -c core -- python -c "$(cat "$DEV_ROOT/tests/P0.E2E/contract_inventory.py")" > "$output"
if [[ "$mode" == --write ]]; then
    cp "$output" "$DEV_ROOT/contracts/baseline.json"
else
    if ! cmp --silent "$output" "$DEV_ROOT/contracts/baseline.json"; then
        diff -u "$DEV_ROOT/contracts/baseline.json" "$output" >&2 || true
        fail 'Pinned route/model/frontend inventory drifted. Inspect it before deliberately refreshing with --write.'
    fi
fi
printf '%s\n' 'Pinned route/model/frontend inventory verified.'
