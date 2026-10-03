# P0 Supervisor contract baseline

References: Supervisor **2026.09.3**, stock Core **2026.9.4**, aiohasupervisor **0.6.0**, frontend **20260826.7**. The [machine-readable inventory](../contracts/baseline.json) records 253 v1 and 222 v2 routes, client dataclass fields/types/requiredness, source hashes and literal calls from 78 bundled frontend assets. Every route is classified. Static extraction describes potential calls, not support for every frontend flow.

The generator executes pinned registration methods with inert handler stubs, preserving version conditions, aliases and log prefixes. Models/assets come from the unmodified Core image. `./hack/refresh-contracts.sh --check` verifies the baseline; `--write` deliberately refreshes it after review. Source downloads are checksum-verified and cached.

| P0 surface | Behavior |
| --- | --- |
| Supervisor ping | Public v1 success envelope |
| Root, Core, Supervisor, host, OS, network info | Typed reads; actual Pod network; null unobserved metadata; unsupported installation identity |
| Core/Supervisor stats | Measured cgroup CPU/memory/block IO and Pod network counters |
| Store/add-ons, jobs, mounts, panels, discovery/services, backup metadata | Empty reads; no advertised lifecycle capability |
| Core/Supervisor options | Validated bootstrap callbacks; atomic durable state |
| Supervisor update | Authenticated no-upgrade HTTP 400 |
| Core `/api/` and `/api/config` | Exact privileged Unix socket reads; query manipulation rejected |
| Other mutations/generic proxies | Deferred or unavailable with explicit errors |
| v2, HAOS and host management | Intentionally unavailable |

Only the Core credential is recognized. Token precedence is `X-Supervisor-Token`, legacy `X-Hassio-Key`, then the final Authorization segment. The gateway has a separate credential. Add-on identities/roles and broader proxy contracts are deferred. `/health/live` and `/operator/info` are project extensions. Operator build version is distinct from the API reference.

Options cover timezone/country/diagnostics and port/SSL/null refresh token. Invalid/unknown values fail without state changes. Legacy `/homeassistant` aliases are included. Upstream GET also permits HEAD; HEAD is deferred here. Static Supervisor `/app` assets are deferred because Core supplies the tested frontend.

Native client parsing, onboarding, loaded integration, Settings/Apps/store, repair visibility, HTTP migration, upload/delete, proxy checks and sequential restart persistence passed in the fresh guarded suite. Development-image validation and real success/failure teardown checks also passed; P0 is complete. See [verification evidence](development.md#coverage-and-evidence). Handler descriptions are deterministic across processes; strict inventory comparison includes callable names, partial arguments, route classifications, client models and frontend hashes.

Expectations derive from released [registrations](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/api/__init__.py), [serialization/token helpers](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/api/utils.py), handlers, installed client models and frontend assets. We did not capture responses from a running upstream Supervisor. Full upstream error wording, optional schemas, route roles and deferred operation semantics are not claimed compatible.
