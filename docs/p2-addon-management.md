# P2 practical add-on management

Work started 2026-10-04 on `feature/p2-addon-management`. P2 here means **Milestone 2** of the implementation plan, following P1 Core lifecycle. The capability table's P2 extensions (local builds, stdin, hardware and audio) remain Milestone 5 work.

The first slice is an internal, fail-closed capability policy for normalized manifest JSON. It resolves a declared Supervisor architecture and concrete version into a prebuilt image reference, reports field-specific blockers, and rejects duplicate or unreviewed fields. Missing Docker-init and automatic-start settings use upstream defaults rather than silently treating them as disabled. Explicit manual boot and `init: false` are required for this initial profile. It rejects permissions, storage mappings, networking, option schemas and other integrations until their adapters exist.

This policy is preliminary screening, not the complete upstream manifest validator or installation authorization. It is not wired into the HTTP catalog yet. A compatible result does not establish image existence, platform support, safe execution, or Home Assistant compatibility. Unknown-field rejection deliberately bounds this profile; upstream removes unknown fields. Repository parsing must preserve field identity, reject duplicates and normalize YAML without dropping unsupported requirements before screening.

Reference contracts are [Supervisor 2026.09.3 manifest validation](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/apps/validate.py), [availability and image resolution](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/apps/model.py), [options](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/apps/options.py), and [store API](https://github.com/home-assistant/supervisor/blob/2026.09.3/supervisor/api/store.py). Availability routes use the native success/error envelope; policy issues are internal data, not a proposed replacement wire format.

The remaining work is ordered so each slice can be reviewed and tested:

1. Pin repository commits and add-on image digests; implement safe repository refresh, manifest normalization, assets and persistent catalog caching. Preserve old catalog data on refresh failure. Add architecture, machine and minimum Core version checks and expose honest availability.
2. Implement Supervisor option-schema validation in C#, including nested values, optional fields, coercion and secret handling. Derive fixtures from the pinned source; avoid advertising unsupported schema features.
3. Introduce add-on intent/operation CRDs, retained data and configuration Secrets. Define ownership, serialization, fencing and finalizers before workload rendering. Keep credentials out of diagnostics and CR options.
4. Render prebuilt workloads, `/data/options.json`, supported directory maps, explicit exposure and security policy. Verify writable shared directories using actual upstream image UIDs. Preserve settings and data across rescheduling and removal.
5. Implement per-add-on tokens, route/method roles and `self`, Core authentication and owned service registration. Add durable install/configure/lifecycle operations and stock UI gates.
6. Prove exact pinned Mosquitto authentication and reachable MQTT service credentials, then File editor shared config writes. File editor's ingress/Core API dependencies must be provided or remain actionable availability blockers; Milestone 3 still owns general ingress support.

Neither Mosquitto nor File editor is currently supported. Their slugs must never bypass capability checks. Full Milestone 2 acceptance requires stock UI install/configure/start/stop/restart/uninstall, correct options, retained settings, role isolation and actual Mosquitto authentication/service behavior in the guarded project cluster.

## Initial verification

On 2026-10-04, locked restore, isolation regressions and all **213 unit tests** passed with no skips or failures (41 new capability-policy cases). Independent read-only review found no actionable P3-or-higher findings. The fresh `./hack/test-cluster.sh` regression suite passed all existing infrastructure, pinned inventory, native client/browser, framework, lifecycle, proxy, upgrade and outage/node-recovery gates. `.test-artifacts/p0.Midvql/suite-result.json` records `exit_code: 0` and `teardown_complete: true`; teardown removed both sealed nodes and private cluster state. The command log is `.test-artifacts/p2-initial-suite.log`.

This verifies the internal policy and preservation of P0/P1 behavior. No add-on was installed or tested, and it does not complete Milestone 2.
