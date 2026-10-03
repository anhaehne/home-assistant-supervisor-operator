# Technology stack decision

Decision date: 2026-10-03.

Status: C#/.NET and the application architecture are accepted. KubeOps is provisional pending a compatibility spike. Exact SDK, package, tool, and image versions will be pinned after that spike. No runtime compatibility has been validated yet.

## Selected stack

| Area | Choice | Purpose |
| --- | --- | --- |
| Language and runtime | C# on .NET 10 LTS | Shared language for the operator, API, gateway, and workers |
| Supervisor-compatible API | ASP.NET Core with explicit JSON contracts | Preserve upstream routes, response envelopes, authorization, streaming, and WebSocket behavior |
| Kubernetes controllers | KubeOps, provisional | Reconciliation, finalizers, CRD/RBAC generation, and admission webhooks |
| Kubernetes access | Official KubernetesClient library | Access Kubernetes resources and workload APIs |
| Durable state | CRDs and Secrets | Persist desired state, operation progress, and credentials |
| File storage | PVCs | Store repository caches, archives, and workload data |
| Gateway and workers | Separate C# executables sharing domain libraries | Isolate dependencies, socket access, and permissions |
| Packaging | Linux containers and Helm | Build and install application components |
| Testing | xUnit, pinned Python Supervisor-client contract tests, Playwright, and isolated kind | Verify logic, wire compatibility, native UI behavior, and real Kubernetes workloads |

.NET 10 is an LTS release supported through November 14, 2028. Pin the SDK for reproducibility and keep supported patch releases current. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

KubeOps provides reconciliation, finalizers, CRD/RBAC generation, and ASP.NET Core admission webhooks. Validate its suitability through the spike below. [KubeOps project](https://github.com/dotnet/dotnet-operator-sdk).

The official Kubernetes C# client supports .NET 10. Select its version together with KubeOps and the tested Kubernetes versions. [Client compatibility matrix](https://github.com/kubernetes-client/csharp).

## Application structure

One ASP.NET Core application hosts the Supervisor-compatible API and Kubernetes controllers in the same process and Deployment. Keep transport handlers, domain rules, and reconciliation in separate internal modules sharing domain types. Preserve the implementation plan's singleton installation and namespace-scoped permissions.

The Core socket gateway runs as a separate minimal C# executable in a sidecar without Kubernetes workload-management credentials. Workers run as separate C# executables in Kubernetes Jobs with task-specific permissions. Shared libraries hold reusable domain and protocol logic.

Home Assistant Core supplies the frontend and native Supervisor integration.

Use explicit API DTOs and serialization rules to preserve field names, nullability, statuses, and error envelopes. Keep upstream wire models separate from CRD models.

CRDs and Secrets hold durable intent, operation state, and credentials. Store large files and archives on PVCs. Implement operation serialization, retry safety, and fencing as application logic; framework queues and leader election alone do not establish these guarantees.

## KubeOps acceptance spike

Before committing to KubeOps, demonstrate these requirements in the isolated kind environment:

1. Namespace-scoped watches and permissions, including denial outside the installation namespace.
2. Structural CRD schemas, status subresources, status updates, and conflict handling.
3. Watch recovery after disconnects and reconciliation after operator restart.
4. Finalizer execution and retry behavior during deletion.
5. API and controllers running together, with accepted operations surviving process interruption.
6. Singleton admission enforcement and explicit operation fencing independent of framework leadership.

Pin the validated KubeOps and KubernetesClient versions, .NET SDK, Kubernetes node image, and development tools after the spike. If KubeOps cannot satisfy these requirements, revisit the framework while retaining C#/.NET and ASP.NET Core.

## Validation and development

Use xUnit for focused unit tests and wire fixtures. Use the pinned baseline Python aiohasupervisor client to verify parsing against the upstream consumer. Python is test tooling; a production helper for exact add-on schema behavior remains an option if compatibility work establishes a need.

Use Playwright against stock Home Assistant Core for onboarding and UI workflows. Run controller, storage, DNS, lifecycle, worker, and end-to-end scenarios in the dedicated isolated kind cluster. Unit tests and mocked Kubernetes clients cannot replace those release gates.

Existing runner isolation requirements remain in force: never use the cluster hosting the development container or its runtime sockets. Provision a nesting-capable runner before full-cluster validation.

See [the implementation plan](implementation-plan.md) for scope, architecture, milestones, and runner requirements.
