# Architecture

This document describes the implemented P0 compatibility spike. The [implementation plan](implementation-plan.md) defines subsequent milestones, and the [technology decision](tech-stack.md) records the accepted C#/.NET stack. P0 proves stock Home Assistant Core and frontend compatibility in an isolated kind cluster. Kubernetes reconciliation, CRDs, Helm packaging and add-on lifecycle remain later work.

## Components and request flow

```mermaid
flowchart LR
    Browser[Stock Home Assistant frontend] --> Core[Stock Core: core-0]
    Core -->|Supervisor API HTTP + Core credential| API[SupervisorOperator MVC controllers]
    API --> Models[P0ReadModels]
    API --> Options[InstanceOptionsStore]
    Options --> State[(Compatibility-state PVC)]
    Core --> Config[(Core config PVC)]
    API -->|Fixed reads + gateway credential| Gateway[CoreGateway MVC controller]
    Gateway -->|Private Unix socket| Core
    API -->|Explicit namespace-scoped pods/exec| Metrics[Core cgroup and Pod counters]
```

Core runs its released image and native entrypoint, loading its built-in `hassio` integration through `SUPERVISOR` and `SUPERVISOR_TOKEN`. Core supplies the UI; the project does not replace or patch that frontend. Its stable Supervisor-managed HTTP listener uses port 80. Tests forward it to runner loopback port 18123.

`SupervisorOperator` hosts the Supervisor-compatible API. `InfoController` provides startup and installation reads, `CoreController` provides Core reads and exact proxy routes, `CapabilitiesController` exposes the empty or unavailable P0 capabilities, and `OptionsController` accepts validated native callbacks. Controllers translate HTTP requests into service calls and explicit response contracts. `SupervisorApi` registers dependencies, controller discovery, JSON settings and credential/error middleware. It does not define business endpoints through minimal API delegates.

`CoreGateway` is a separate sidecar executable. Its controller exposes health, measured Pod network metadata, Core status and Core configuration. `CoreSocketClient` connects only to the configured absolute Unix socket, and offers two fixed reads: `/api/` and `/api/config`. It never falls back to TCP. Controller discovery is restricted to each application's assembly, including in test hosts.

| Location | Responsibility |
| --- | --- |
| `src/Supervisor.Contracts` | Explicit upstream wire DTOs and success/error envelopes |
| `src/SupervisorOperator` | API controllers, credential/error middleware, read models, option persistence and Core metrics |
| `src/CoreGateway` | Authenticated, narrowly scoped access to Core's private socket |
| `src/SharedRuntime` | Shared measured cgroup and network-counter helpers |
| `deploy/p0.yaml` | P0 singleton workloads, Services, PVCs, credentials references and namespace-scoped RBAC |
| `tools/DevGuard` and `hack/` | Runtime identity, sealed kubeconfig, network guards and disposable test lifecycle |
| `contracts/baseline.json` | Committed compatibility inventory used by verification, not runtime routing |
| `tests/` | Wire-contract/state/isolation tests and native client/browser E2E scenarios |

## Contracts and errors

The Supervisor API serializes snake_case names and retains meaningful null metadata. Successful reads use `{ "result": "ok", "data": ... }`; rejected operations use `{ "result": "error", "message": ... }` with explicit error status codes. Exact Core proxy reads preserve the upstream response body, content type and status instead of wrapping it. Gateway reads likewise preserve Core's response.

The API supports native `/core` and legacy `/homeassistant` aliases. Options accept a bounded subset and validate the whole body before updating state. Manual body parsing preserves native empty-body, malformed-JSON and unsupported-field behavior rather than introducing MVC's default validation response format. Update requests return the tested no-upgrade error. Unknown capabilities and v2 routes do not claim success.

The [contract baseline](contract-baseline.md) records pinned upstream routes, client models and bundled frontend calls, with each route classified. Strict regeneration detects drift. Static inventory coverage and native behavior coverage are separate evidence; neither establishes compatibility with every upstream operation.

## State and lifecycle

`InstanceOptionsStore` stores the spike's timezone, country, diagnostics and Core HTTP option callbacks. Updates serialize under a semaphore and write through a temporary file before atomic replacement. The API's PVC survives its restart. Core has a separate configuration PVC for native users, settings and storage. Tests replace Core sequentially; they never start overlapping installations.

The P0 manifests declare one Supervisor API Deployment and one Core StatefulSet with a gateway sidecar. Core uses `OnDelete` updates; the API uses `Recreate`. The gateway Service publishes not-ready addresses so startup reads can reach metadata without a readiness cycle. Later milestones add reconciled desired state, durable operations and recovery. The current option store is a compatibility-spike mechanism, not implemented CRD ownership or general Core lifecycle control.

## Credentials and isolation

Core's credential authenticates the Supervisor API, with the native header precedence preserved. Only ping and liveness are public. The gateway has a separate credential and rejects query parameters. Caller-selected upstream paths, credentials and commands are not accepted. Credentials are compared using constant-time hashes and never belong in committed fixtures or exported diagnostics.

The API runs non-root. The gateway runs as root because stock Core's Unix socket is root-owned with mode 0600; it drops all capabilities, uses a read-only root filesystem and has no Kubernetes service-account credentials. The Core workload also disables service-account token mounting.

Core metrics use a fixed read-only exec command against `core-0` in the installed namespace. Only the installed API workload explicitly opts into in-cluster credentials, using its downward-API namespace and narrow RBAC. Local API runs cannot select ambient hosting-cluster credentials. Statistics represent actual container/Pod counters; host disk values describe the compatibility-state filesystem. Unobserved node and HAOS metadata remain null, and the installation reports unsupported status with visible limitations.

Project tests use only the dedicated nested Docker daemon and a verified project-owned kind cluster. DevGuard seals daemon/node identity and generated kubeconfig, rejects ambient credentials and remote runtime inputs, and checks bridge MTU against the runner interface. These configuration guards do not establish platform firewall enforcement or production hardware/LAN support.

## Verification boundary

The fresh suite owns setup, image loading, real scheduling/DNS/PVC Jobs, inventory comparison, RBAC/client checks, browser scenarios, diagnostics and teardown. Browser scenarios exercise native onboarding, existing-account startup, Settings/Apps/store, repairs, uploads, proxy behavior, restart persistence and HTTP migration. A separate injected-failure gate checks real cleanup. See [development](development.md) for commands and actual results.

P0 does not provide add-on installation, backups, updates, ingress, hardware management, CRDs or Kubernetes reconcilers. KubeOps acceptance remains a P1 requirement; the HTTP controllers introduced here are ASP.NET Core transport controllers.

## Deployment and P0 closeout

The supported P0 deployment path is the guarded development runner: `dev-up.sh` creates a project-owned kind cluster, and `dev-load.sh` builds and loads local application images, creates credentials, and applies `deploy/p0.yaml`. The manifest uses the fixed `haso-p0` namespace and preloaded images with `imagePullPolicy: Never`. It is a development fixture, not a user installation package. The full suite owns this deployment and removes it after verification; no test installation remains running after closeout.

P0 closes with MVC controller-based HTTP services, the pinned compatibility inventory, architecture/contribution documentation and green native client/browser tests. The [development evidence](development.md#coverage-and-evidence) records 70 passing .NET tests, the fresh full E2E run, development-image checks and verified success/failure teardown.

P1 must validate KubeOps, introduce CRDs and namespace-scoped reconciliation, implement durable Core lifecycle operations and singleton admission, and provide Helm packaging with install/uninstall and retained-config checks. There is currently no Helm chart, application image release pipeline or ingress/TLS installation configuration. Later milestones add add-on lifecycle, ingress, backups and updates; closing P0 does not deliver those features.
