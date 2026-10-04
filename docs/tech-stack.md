# Technology stack decision

Decision date: 2026-10-03.

Status: C#/.NET and the application architecture are accepted. SDK, dependencies, tools and application images are pinned for the completed P0 stock Core compatibility spike. Development-image checks, the fresh full native client/browser suite and success/failure teardown passed in isolated kind on 2026-10-03. KubeOps.Operator 13.3.1 with KubernetesClient 19.0.2 passed P1 acceptance on 2026-10-04, including lifecycle, admission, fencing, finalizers, conflict handling, watch/restart recovery and prolonged API-read/dependency outages in Kubernetes 1.37.0.

## Selected stack

| Area | Choice | Purpose |
| --- | --- | --- |
| Language and runtime | C# on .NET 10 LTS | Shared language for the operator, API, gateway, and workers |
| Supervisor-compatible API | ASP.NET Core MVC controllers with explicit JSON contracts | Preserve upstream routes, response envelopes, authorization, streaming, and WebSocket behavior |
| Kubernetes controllers | KubeOps.Operator 13.3.1, validated for P1 | Reconciliation, finalizers, CRD/RBAC generation, and admission webhooks |
| Kubernetes access | Official KubernetesClient library | Access Kubernetes resources and workload APIs |
| Durable state | CRDs and Secrets | Persist desired state, operation progress, and credentials |
| File storage | PVCs | Store repository caches, archives, and workload data |
| Gateway and workers | Separate C# executables sharing domain libraries | Isolate dependencies, socket access, and permissions |
| Packaging | Linux containers and Helm | Build and install application components |
| Testing | xUnit with C# contract fixtures, Playwright for .NET, and isolated kind | Verify logic, wire compatibility, native UI behavior, and real Kubernetes workloads |

.NET 10 is an LTS release supported through November 14, 2028. Pin the SDK for reproducibility and keep supported patch releases current. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

KubeOps provides reconciliation, finalizers, CRD/RBAC generation, and ASP.NET Core admission webhooks. The acceptance checks below passed for the implemented P1 architecture. [KubeOps project](https://github.com/dotnet/dotnet-operator-sdk).

The official Kubernetes C# client supports .NET 10. Select its version together with KubeOps and the tested Kubernetes versions. [Client compatibility matrix](https://github.com/kubernetes-client/csharp).

## Application structure

One ASP.NET Core application hosts the Supervisor-compatible API and Kubernetes controllers in the same process and Deployment. HTTP endpoints use MVC controllers in both API services. Keep transport handlers, domain rules, and reconciliation in separate internal modules sharing domain types. Preserve the implementation plan's singleton installation and namespace-scoped permissions. See [architecture](architecture.md) for the current implementation.

The Core socket gateway runs as a separate minimal C# executable in a sidecar without Kubernetes workload-management credentials. Workers run as separate C# executables in Kubernetes Jobs with task-specific permissions. Shared libraries hold reusable domain and protocol logic.

Home Assistant Core supplies the frontend and native Supervisor integration.

Use explicit API DTOs and serialization rules to preserve field names, nullability, statuses, and error envelopes. Keep upstream wire models separate from CRD models.

CRDs and Secrets hold durable intent, operation state, and credentials. Store large files and archives on PVCs. Implement operation serialization, retry safety, and fencing as application logic; framework queues and leader election alone do not establish these guarantees.

## KubeOps acceptance spike

The [P1 foundation spike](p1-foundation.md) pins KubeOps.Operator 13.3.1 with KubernetesClient 19.0.2 and implements namespace-scoped Core reconciliation/finalizers in the combined API process. Fresh full acceptance passed in `.test-artifacts/p0.zEExGB/`, including all six requirements below. This validates the development preview on Kubernetes 1.37.0; production CSI, LAN/hardware profiles and other Kubernetes versions remain unvalidated.

The isolated kind acceptance suite demonstrated these requirements:

1. Namespace-scoped watches and permissions, including denial outside the installation namespace.
2. Structural CRD schemas, status subresources, status updates, and conflict handling.
3. Watch recovery after disconnects and reconciliation after operator restart.
4. Finalizer execution and retry behavior during deletion.
5. API and controllers running together, with accepted operations surviving process interruption.
6. Singleton admission enforcement and explicit operation fencing independent of framework leadership.

Pin the validated KubeOps and KubernetesClient versions, .NET SDK, Kubernetes node image, and development tools after the spike. If KubeOps cannot satisfy these requirements, revisit the framework while retaining C#/.NET and ASP.NET Core.

## Validation and development

Use xUnit for focused unit tests and C# wire-contract fixtures derived from the pinned upstream contracts. Verify actual upstream client parsing through stock Home Assistant Core in end-to-end tests. Its container supplies its own Python runtime and aiohasupervisor dependency; no standalone Python installation is required in the development image. Use direct shell helpers and `dotnet test` rather than Make. Revisit additional tooling only if a demonstrated compatibility gap requires it.

Use Playwright for .NET against stock Home Assistant Core for onboarding and UI workflows. Run controller, storage, DNS, lifecycle, worker, and end-to-end scenarios in the dedicated isolated kind cluster. Unit tests and mocked Kubernetes clients cannot replace those release gates.

Existing runner isolation requirements remain in force: never use the cluster hosting the development container or its runtime sockets. Provision a nesting-capable runner before full-cluster validation.

See [the implementation plan](implementation-plan.md) for scope, architecture, milestones, and runner requirements.
