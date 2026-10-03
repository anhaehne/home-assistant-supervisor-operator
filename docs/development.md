# Development

The P0 compatibility spike is implemented. Stock Core 2026.9.4 completed native onboarding, loaded `hassio`, rendered Settings/Apps/store, exposed installation warnings, accepted native uploads, and recovered through sequential API/Core restarts in the guarded local cluster. Native client parsing, measured statistics, socket authorization and legacy HTTP migration passed.

**P0 is complete as of 2026-10-03.** The platform corrected the dedicated daemon's MTU configuration; its default bridge and newly created kind network passed the guard against the runner's 1450 MTU. The development image passed pinned toolchain and Chromium/Firefox/WebKit smoke checks. A fresh complete suite returned zero with successful teardown, and the separate injected-failure test verified cleanup after expected exit 41. See the [durable runner configuration](../dev/runner-profile.md#durable-mtu-configuration).

## Pinned inputs

| Input | Version |
| --- | --- |
| .NET SDK / runtime | 10.0.401 / 10.0.12 |
| KubernetesClient / Playwright for .NET | 19.0.2 / 1.63.0 |
| Docker / Buildx | 29.8.1 / 0.37.2 |
| kind / Kubernetes / kubectl / Helm | 0.33.0 / 1.37.0 / 1.37.0 / 3.19.0 |
| Core / API reference / native client / frontend | 2026.9.4 / 2026.09.3 / 0.6.0 / 20260826.7 |

SDK and NuGet dependencies have lock files. Core, application bases, kind node and smoke image have digest pins. Tool downloads have checksum pins. The initial target is linux/amd64 and Kubernetes 1.37.0; minimum production Kubernetes support, other architectures, production CSI, hardware and LAN behavior remain unvalidated. KubeOps acceptance is P1 work.

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

`test-cluster.sh` owns fresh setup, image loading, infrastructure checks, inventory verification, RBAC/client integration, browser scenarios, diagnostics and teardown. It refuses existing state. Exit zero requires all invoked gates and cleanup to succeed. `test-teardown.sh` creates a real cluster, injects failure 41 after setup and requires automatic cleanup. Both passed. Build workers explicitly close the inherited cluster-lock descriptor, so persistent compiler processes cannot block subsequent helpers. Contract inventory serialization preserves callable names and partial arguments without process-specific memory addresses; drift checks remain strict and report a diff.

For interactive work: `dev-up.sh`, `dev-load.sh`, `test-runner.sh`, `refresh-contracts.sh --check`, `test-integration.sh`, `test-e2e.sh`, then `dev-down.sh`. Default browser mode requires un-onboarded Core. An account created by this harness can use `test-e2e.sh ARTIFACT_DIRECTORY existing`. The focused `http-migration` phase selects one scenario; the full suite always runs all phases from fresh state.

## Coverage and evidence

**70 .NET tests passed, none skipped.** Tests cover auth/envelopes, controller JSON names/nulls, legacy aliases, proxy query rejection, gateway credential/socket boundaries, invalid callbacks without state changes, independent startup reads, concurrent durable state, measured-counter parsing, manifest completeness, isolation and MTU. Poisoned ambient kubeconfigs, existing-cluster flags and remote runtime inputs were rejected without runtime/Kubernetes requests, including the suite entrypoint. Local API execution never selects ambient hosting-cluster credentials; the installed metrics adapter requires explicit in-cluster opt-in and its downward-API namespace.

Browser checks create a real owner account in the released frontend, verify loaded integration state and installation/project repair warnings, and render Settings/Apps/store with an honestly empty catalog. They perform actual multipart upload/delete and forwarded-request checks. API options survive API restart; Core retains its user/config through sequential Pod replacement. A separate singleton fixture imports legacy HTTP YAML, verifies and promotes the native pending trial, rejects untrusted proxies and retains stored settings after YAML removal. Tests do not edit private Core storage.

The shipped aiohasupervisor client verifies required startup/read models, measured cgroup/Pod statistics, no-update behavior, exact socket-backed reads, authentication and rejected mutations/proxy targets. RBAC permits only exec into `core-0` in the installation namespace; other Pods/namespaces, Secrets and workload creation are denied. Infrastructure checks use real Jobs, DNS and dynamically provisioned PVCs.

Artifacts are private and ignored under `.test-artifacts/`: unit TRX, result JSON, screenshots, redacted logs/events, image/node identifiers and teardown results. The fresh green run after the MVC controller refactor is `.test-artifacts/p0.ELiech/`, with 70 passing .NET tests and `suite-result.json` reporting `exit_code: 0` and `teardown_complete: true`; its command log is `.test-artifacts/controllers-suite.log`. Its four browser results cover default and migrated HTTP configurations, each with fresh onboarding and existing-account startup. The original P0 acceptance run is `.test-artifacts/p0.VS183Z/`. `.test-artifacts/p0.VSRN6p/` records the injected-failure run (`exit_code: 41`, `teardown_complete: true`); the wrapper reported success. `.test-artifacts/full-suite.log`, `teardown-suite.log` and `dev-image-build.log` record the original gates. Earlier failed runs retain diagnostics and also completed cleanup. `.dev-state/` holds private credentials/kubeconfig/browser account data and is deleted on cleanup. Credential values and browser storage are not exported.

## Spike boundaries

HTTP endpoints use ASP.NET Core MVC controllers in both services. This preview has no Kubernetes reconcilers, CRDs, Helm release, add-on lifecycle, ingress, backups, updates or HAOS/node management. Options use an atomic compatibility-state PVC store; P1 moves intent behind CRDs. The operator is non-root. The gateway matches stock Core's root-owned mode-0600 socket, drops all capabilities, has no Kubernetes credentials and mounts only the socket plus temporary storage. It exposes exact reads, never caller-selected URLs or commands. See [architecture](architecture.md) and [contribution guidance](../CONTRIBUTING.md).

Core uses its stock image/entrypoint and Supervisor HTTP port **80**. The temporary 8123 onboarding bridge is not a deployment dependency. Bootstrap reads avoid Core HTTP readiness; gateway metadata comes from the actual Pod and its Service publishes not-ready addresses. Core counters use a fixed namespace-scoped exec command, with no PID sharing. Host disk counters describe the state PVC filesystem; unobserved node/HAOS metadata stays null. No upstream-supported installation is claimed.

Runtime access uses the dedicated socket and recorded daemon/node identities. Every kubectl call uses an explicit sealed kubeconfig/context and loopback API. Child environments discard hosting-cluster discovery and cloud credentials. Cleanup checks node/data-volume removal. The outer firewall and resource reservations remain platform responsibilities. The durable setup requirement is in [AGENTS.md](../AGENTS.md).
