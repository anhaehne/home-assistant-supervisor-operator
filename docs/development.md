# Development

The P0 compatibility spike is implemented. Stock Core 2026.9.4 completed native onboarding, loaded `hassio`, rendered Settings/Apps/store, exposed installation warnings, accepted native uploads, and recovered through sequential API/Core restarts in the guarded local cluster. Native client parsing, measured statistics, socket authorization and legacy HTTP migration passed.

**P0 is complete as of 2026-10-03.** The platform corrected the dedicated daemon's MTU configuration; its default bridge and newly created kind network passed the guard against the runner's 1450 MTU. The development image passed pinned toolchain and Chromium/Firefox/WebKit smoke checks. A fresh complete suite returned zero with successful teardown, and the separate injected-failure test verified cleanup after expected exit 41. See the [durable runner configuration](../dev/runner-profile.md#durable-mtu-configuration).

## Pinned inputs

| Input | Version |
| --- | --- |
| .NET SDK / runtime | 10.0.401 / 10.0.12 |
| KubernetesClient / Playwright for .NET | 19.0.2 / 1.63.0 |
| KubeOps.Operator (validated P1) | 13.3.1 |
| Docker / Buildx | 29.8.1 / 0.37.2 |
| kind / Kubernetes / kubectl / Helm | 0.33.0 / 1.37.0 / 1.37.0 / 3.19.0 |
| Core / API reference / native client / frontend | 2026.9.4 / 2026.09.3 / 0.6.0 / 20260826.7 |

SDK and NuGet dependencies have lock files. Core, application bases, kind node and smoke image have digest pins. Tool downloads have checksum pins. The initial target is linux/amd64 and Kubernetes 1.37.0; minimum production Kubernetes support, other architectures, production CSI, hardware and LAN behavior remain unvalidated. KubeOps acceptance passed with P1 on 2026-10-04.

This managed environment requires supported sandbox escalation for downloads, .NET IPC, runtime access and browsers. Repair denied/missing prerequisites or escalate them; do not disable checks. Make and a standalone Python installation are not prerequisites. Native Python checks run inside stock Core. Matching Playwright browsers are supplied at `/ms-playwright`.

## Commands

Fast checks work independently of the runtime:

```sh
dotnet restore --locked-mode
dotnet test --no-restore
./hack/test-isolation.sh
```

For full development acceptance, run serially:

```sh
./hack/install-buildx.sh
./hack/dev-up.sh
./hack/build-dev-image.sh
./hack/dev-down.sh
./hack/test-teardown.sh
./hack/test-cluster.sh
```

`test-cluster.sh` owns fresh two-node kind setup, image loading, infrastructure checks, inventory verification, RBAC/client integration, the framework probe, native browser scenarios and installed P1 lifecycle/upgrade/node-fault gates, diagnostics and teardown. P0's Core is stopped before P1 is installed, so the scenarios remain sequential. It refuses existing state. Exit zero requires every gate and cleanup to succeed. `test-teardown.sh` injects failure 41 after real setup and requires both sealed nodes and their data volumes to be removed. Build workers close inherited cluster-lock descriptors. Contract inventory comparisons remain strict; refreshing requires reviewing the deliberate route/model diff.

For interactive work: `dev-up.sh`, `dev-load.sh`, `test-runner.sh`, `refresh-contracts.sh --check`, `test-integration.sh`, `test-framework.sh`, `test-e2e.sh`, then `dev-down.sh`. Default browser mode requires un-onboarded Core. An account created by this harness can use `test-e2e.sh ARTIFACT_DIRECTORY existing`. The focused `http-migration` phase selects one scenario; the full suite always runs all phases from fresh state.

After P0 validation, `./hack/test-p1.sh ARTIFACT_DIRECTORY` stops the compatibility fixture, installs the chart, runs lifecycle/native UI checks, immutable upgrades/schema defaults and the watch/worker fault gate. For P1 inventory work, use `HASO_TEST_NAMESPACE=haso-p1 ./hack/refresh-contracts.sh --check`. The node fault helper requires the sealed worker and intentionally disconnects/stops only that node in the dedicated nested runtime. It cannot use an arbitrary existing cluster. Failed prerequisites remain errors.

## Coverage and evidence

**70 .NET tests passed, none skipped.** Tests cover auth/envelopes, controller JSON names/nulls, legacy aliases, proxy query rejection, gateway credential/socket boundaries, invalid callbacks without state changes, independent startup reads, concurrent durable state, measured-counter parsing, manifest completeness, isolation and MTU. Poisoned ambient kubeconfigs, existing-cluster flags and remote runtime inputs were rejected without runtime/Kubernetes requests, including the suite entrypoint. Local API execution never selects ambient hosting-cluster credentials; the installed metrics adapter requires explicit in-cluster opt-in and its downward-API namespace.

Browser checks create a real owner account in the released frontend, verify loaded integration state and installation/project repair warnings, and render Settings/Apps/store with an honestly empty catalog. They perform actual multipart upload/delete and forwarded-request checks. API options survive API restart; Core retains its user/config through sequential Pod replacement. A separate singleton fixture imports legacy HTTP YAML, verifies and promotes the native pending trial, rejects untrusted proxies and retains stored settings after YAML removal. Tests do not edit private Core storage.

The shipped aiohasupervisor client verifies required startup/read models, measured cgroup/Pod statistics, no-update behavior, exact socket-backed reads, authentication and rejected mutations/proxy targets. The metrics Role permits only exec into `core-0` in the installation namespace; other Pods/namespaces, Secrets and workload creation are denied. A separate P1 Role permits namespace-scoped probe reconciliation and updates to its fixed state ConfigMap. Infrastructure checks use real Jobs, DNS and dynamically provisioned PVCs.

Artifacts are private and ignored under `.test-artifacts/`: unit TRX, result JSON, screenshots, redacted logs/events, image/node identifiers and teardown results. The fresh green run after the MVC controller refactor is `.test-artifacts/p0.ELiech/`, with 70 passing .NET tests and `suite-result.json` reporting `exit_code: 0` and `teardown_complete: true`; its command log is `.test-artifacts/controllers-suite.log`. Its four browser results cover default and migrated HTTP configurations, each with fresh onboarding and existing-account startup. The original P0 acceptance run is `.test-artifacts/p0.VS183Z/`. `.test-artifacts/p0.VSRN6p/` records the injected-failure run (`exit_code: 41`, `teardown_complete: true`); the wrapper reported success. `.test-artifacts/full-suite.log`, `teardown-suite.log` and `dev-image-build.log` record the original gates. Earlier failed runs retain diagnostics and also completed cleanup. `.dev-state/` holds private credentials/kubeconfig/browser account data and is deleted on cleanup. Credential values and browser storage are not exported.

## Spike boundaries

The first P1 foundation run passed on 2026-10-03: **81 .NET tests, none skipped**, the real namespace-scoped KubeOps probe/status/finalizer gate, and the complete native client/browser suite. `.test-artifacts/p0.bVwRPA/` records `exit_code: 0` and successful teardown. The separate teardown wrapper passed with deliberate inner exit 41 and successful cleanup in `.test-artifacts/p0.zk2Zst/`. The earlier `.test-artifacts/p0.iVwbOf/` failed at finalizer registration and also cleaned up; the corrected registration has a unit regression and passed the real deletion/retry gate. See [P1 scope and acceptance](p1-foundation.md). The existing artifact names retain the P0 harness prefix.

HTTP endpoints use ASP.NET Core MVC controllers in both services. The [P1 foundation](p1-foundation.md) now implements Core lifecycle, instance/operation CRDs, jobs/basic logs, namespace reconciliation, singleton admission, GitOps ownership, finalizers and a development-preview Helm chart. Full fresh acceptance passed on 2026-10-04 in `.test-artifacts/p0.zEExGB/`: **111 .NET tests, none skipped**, every P0/P1 infrastructure, native client/browser, lifecycle, upgrade, watch, worker-partition and prolonged dependency/API-read recovery gate, with `exit_code: 0` and `teardown_complete: true`. The previous persistent-runtime run `.test-artifacts/p0.FsD7so/` caught a scale admission error for omitted zero replicas and cleaned up successfully; the corrected chart passed the complete fresh suite. The injected-failure wrapper also passed: `.test-artifacts/p0.udReFG/suite-result.json` records deliberate inner exit 41 and successful teardown; the wrapper exited zero. `.test-artifacts/p1-persistent-dev-image.log` records passing toolchain and Chromium/Firefox/WebKit smoke checks on the repaired persistent runtime. Installed options use the instance CR; P0/local fixtures retain the atomic file store. Add-ons, ingress authentication, backups, updates and HAOS/node management remain later work. The non-root operator shares a process with KubeOps. The gateway matches stock Core's root-owned mode-0600 socket, drops capabilities, has no Kubernetes credentials and exposes only fixed socket reads. See [architecture](architecture.md) and [contribution guidance](../CONTRIBUTING.md).

Core uses its stock image/entrypoint and Supervisor HTTP port **80**. The temporary 8123 onboarding bridge is not a deployment dependency. Bootstrap reads avoid Core HTTP readiness; gateway metadata comes from the actual Pod and its Service publishes not-ready addresses. Core counters use a fixed namespace-scoped exec command, with no PID sharing. Host disk counters describe the operator filesystem (P0 state PVC or P1 temporary filesystem), never Core PVC quotas; unobserved node/HAOS metadata stays null. No upstream-supported installation is claimed.

Runtime access uses the dedicated socket and recorded daemon/node identities. Every kubectl call uses an explicit sealed kubeconfig/context and loopback API. Child environments discard hosting-cluster discovery and cloud credentials. Cleanup checks node/data-volume removal. The outer firewall and resource reservations remain platform responsibilities. The durable setup requirement is in [AGENTS.md](../AGENTS.md).

On 2026-10-04 at 14:45 UTC, a fresh confirmation run passed on the persistent Docker runner: `.test-artifacts/p0.jXSyWT/suite-result.json` records `exit_code: 0` and `teardown_complete: true`. All 111 unit tests passed with no skips, followed by every infrastructure, isolation, native-client, framework, browser and P1 lifecycle/upgrade/dependency/worker-fault/API-read recovery gate. Both sealed kind nodes, their data volumes and `.dev-state` were removed; no outer-runner interruption occurred. The command log is `.test-artifacts/p1-confirmation-20261004T142413Z.log`. The first attempt `.test-artifacts/p0.IJUIzt/` stopped before cluster creation because the outer Paseo image lacked `rg`; it is failed evidence, not acceptance. The rerun explicitly used `PATH="$PWD/.dev-tools:$PATH" ./hack/test-cluster.sh`, with ripgrep 14.1.0 extracted from the already validated `haso/dev:p0` image (`sha256:bfb1b09d6604ab3014616b5da62ff16903aff107d0a57f8ddb1bc369b66b7c9d`). The platform repository added ripgrep to future runner builds in commit `c396dff`; the current runner image was not rebuilt during confirmation.

## Dedicated sidecar restart recovery

Sidecar restarts are expected. Shared preflight waits up to 60 seconds for the fixed dedicated socket. `./hack/recover-runtime.sh` checks the replacement daemon and MTU, then looks for project nodes by cluster label, exact recorded names and sealed full IDs. Retained nodes must match every recorded ID/name/label, and their kubeconfig remains sealed before any API access. Changed or partial nodes fail closed.

If all recorded nodes are absent after a daemon replacement, the helper archives `.dev-state` under a private `.test-artifacts/runtime-restart.*/private-state` directory and a fresh suite can start. It does not reuse the old endpoint/credentials, delete unrelated resources, or claim the old PVC survived. `test-cluster.sh` invokes this recovery before its existing-state check. An interrupted active suite must be rerun after recovery; only a complete fresh run with verified cleanup counts as acceptance. This recovery is authorized as the normal workflow and does not need repeated confirmation.

Current source removes Core node pinning. The fresh full acceptance run on 2026-10-04 in `.test-artifacts/p0.VS5OM8/` passed all **112 unit tests, none skipped**, every P0/P1 gate and cleanup (`exit_code: 0`, `teardown_complete: true`). The upgrade gate removes a simulated legacy-alpha selector through orderly replacement while retaining the PVC. The node-fault gate partitions the running worker, refuses replacement before physical fencing, then cordons that worker and recovers Core on the other sealed node with the same PVC/PV and retained configuration. Both nodes mount a project-owned shared Docker volume for the static test PV; cleanup removes that volume as well as node data volumes. This validates controller/scheduler recovery with shared test storage, not production CSI attachment or hardware/LAN portability. Published `0.1.0-alpha.1` predates this change.

The updated fixture also passed injected-failure cleanup in `.test-artifacts/p0.ZZeINw/`: the intentional inner exit was 41, `teardown_complete` was true and the wrapper exited zero. Final guarded Docker storage inspection showed zero containers and zero local volumes, with 7.516 GB of image caches and 600.4 MB of build cache retained. Final code review found no remaining P3-or-higher issues.

On 2026-10-04, the unreleased managed ingress-proxy implementation passed the full fresh suite in `.test-artifacts/p0.rI2JHf/`: **147 unit tests passed, none skipped**, all isolation/infrastructure/native-client/framework/browser/P1 gates passed, and `suite-result.json` records `exit_code: 0` and `teardown_complete: true`. Existing-PVC acceptance changed trust from proxy A to B, revoked it with `[]`, and released management with `null`, while preserving configuration, owner records and PVC/PV identity. Fresh installation configured trust before onboarding, accepted only the selected source, survived Core replacement and retained its PVC during graceful uninstall. Both sealed kind nodes and all owned test data volumes were removed. The earlier superseded-image run `.test-artifacts/p0.C8Dc64/` is incomplete evidence (exit 143); its exact retained identity was revalidated before explicit guarded cleanup. Published alpha.2 artifacts remain unchanged; deploy this fix only through a new reviewed chart with matching images and both updated CRDs.

On 2026-10-04, the recommended `ingress.trustPodNetwork: true` extension passed fresh full acceptance in `.test-artifacts/p0.wOQyrQ/` (`exit_code: 0`, `teardown_complete: true`). The final unit regression run in `.test-artifacts/pod-network-final-unit/pod-network-final.trx` passed **172 tests with no skips or failures**. Default acceptance verified real forwarded requests, exact discovered CIDR union, read-only Node RBAC, two API-only network fixtures added and removed individually, native trial promotion and unchanged PVC/PV/retained data. Existing explicit allowlist, revocation, fresh install, browser, lifecycle, upgrade and fault-recovery gates also passed. Both sealed runtime nodes, all owned test volumes and private state were removed. Independent review found no remaining P3-or-higher findings. This source was subsequently published as alpha.3 after successful published-artifact acceptance; published alpha.2 artifacts are unchanged. See [release evidence](../packaging/RELEASING.md#third-prerelease).
