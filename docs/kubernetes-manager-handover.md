# Kubernetes development runner handover

Current status (2026-10-03): the dedicated runtime is provisioned, the durable MTU correction is verified, and P0 development-image/full-suite/teardown acceptance passed. The initial handover and smoke observations below are retained as historical context; current evidence is in [development](development.md).

Recipient: Kubernetes platform manager. Date: 2026-10-03.

Please provision a dedicated development runner capable of running a complete, disposable one-node kind cluster using its own nested container runtime. This enables development and integration testing of the Home Assistant Supervisor Operator. The repository is currently design only; application deployment and test scripts are still to be implemented.

The development container must never use the Kubernetes cluster hosting it for application testing. Provision a new runner profile rather than changing the hosting node from inside the current container.

## Initial environment assessment

Local inspection found the following; no Kubernetes API was contacted.

| Item | Observed state |
| --- | --- |
| Application tooling | .NET SDK 10.0.401 and ASP.NET Core runtime 10.0.12 installed |
| Kubernetes tooling | kubectl 1.37.0 and Helm 3.19.0 installed; versions are not yet validated against a test cluster |
| Resources | CPU limit equivalent to 2 CPUs, memory limit 4 GiB, approximately 20 GiB free workspace storage |
| Nested runtime | Docker, Podman, and kind absent; no standard Docker/containerd socket found |
| Kernel access | cgroup v2 present but not writable; effective capabilities empty; user namespace UID mapping fails because the mapping filesystem is read-only |

At the initial inspection, full nested-cluster testing was blocked. The subsequent verification below establishes that the newly supplied dedicated runtime can run the infrastructure smoke tests.

## Subsequent runner verification

On 2026-10-03, the supplied Docker endpoint at `/run/paseo-docker/docker.sock` was verified as a socket mounted from this Pod's temporary volume. The daemon name matches the runner Pod and initially reported zero containers. Docker 29.8.1 reports 8 CPUs and approximately 15 GiB memory visible to the daemon; platform resource requests and limits still need confirmation. The development container itself remains limited to 2 CPUs and 4 GiB. Runtime access from this session required sandbox escalation.

A fresh, uniquely named cluster passed the following checks:

- kind 0.33.0 created a Ready one-node Kubernetes 1.37.0 cluster with API bound to `127.0.0.1`.
- Node image: `kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5`.
- BusyBox 1.37.0 was loaded from the dedicated runtime into the node. Two real Jobs completed successfully.
- Service DNS resolved `kubernetes.default.svc.cluster.local`.
- The local-path provisioner bound a 64 MiB PVC; a second Pod read the first Pod's persisted test value.
- Cleanup removed the named node container and its recorded data volume. Cached images and the runtime's shared kind network were retained.

All Kubernetes commands used an explicit generated kubeconfig and context, with a sanitized environment. The kubeconfig was checked for the expected cluster identity, loopback server, and absence of credential exec plugins. No hosting-cluster API was contacted. The standard outer service-account token path was absent, but the complete Pod mount and network policy configuration was not inspected through the hosting API.

Remaining platform checks: confirm runtime resource allocation and storage lifecycle, enforce the hosting-network isolation boundary, and provide a supported runtime access path for automated tests. Playwright for .NET 1.63.0 and browser binaries are installed at `/opt/paseo-playwright` and `/ms-playwright`. Chromium, Firefox, and WebKit passed local launch, rendering, JavaScript, and click smoke tests. Browser execution required sandbox escalation; the command sandbox blocked Chromium with an operation-not-permitted error. Full application compatibility remains unvalidated. Existing development-container cgroup restrictions did not prevent this dedicated runtime from passing the smoke tests.

## Requested runner

1. Allocate an initial budget of 4 CPUs, 8 GiB RAM, and at least 20 GiB writable space for images, runtime storage, and disposable test volumes. These are planning estimates, to be measured during the first spike. Account for image-cache growth separately.
2. Provide a dedicated Docker or Podman runtime with its own daemon or runtime process, endpoint, and writable storage. Prefer rootless operation if user namespaces, UID/GID mapping, cgroup delegation, and networking prerequisites can be supported and validated. If that cannot work, provide an explicitly approved runner for privileged nesting or an independently provisioned isolated CI runner. Do not make the ordinary development Pod privileged by default.
3. Install pinned .NET 10 SDK, Git, a shell, kind, kubectl, Helm, and the chosen runtime. Provide Playwright for .NET browser dependencies for end-to-end testing. The project will select and record validated tool and node-image versions during the compatibility spike.
4. Allow the nested runtime to support a real Kubernetes node, including its networking, DNS, and disposable dynamic PVC provisioning. Verify this by creating a kind cluster and running workloads; binary presence alone is insufficient.
5. Keep API and forwarded application ports on runner loopback. If remote developer access is needed, provide an authenticated tunnel to loopback rather than exposing the API or Home Assistant publicly.

Make, Go, and a standalone Python installation are not required for this profile. Use a prebuilt kind binary, `dotnet test`, direct shell helpers, and Playwright for .NET. Stock Home Assistant Core includes its own Python runtime and upstream client; those remain part of the application image used in compatibility tests.

## Isolation requirements

- Set `automountServiceAccountToken: false` on the new outer runner Pod. Do not mount hosting-cluster kubeconfigs, cloud credentials, or host Docker/containerd sockets.
- Keep host networking and host PID namespaces disabled. Any nesting-specific permissions must be documented in the dedicated runner profile.
- Restrict outer-runner egress to prevent access to the hosting-cluster API, node management endpoints, and cloud metadata. Account for direct IP access and IPv6 where enabled. Allow explicitly required dependency/image downloads; prefer preloaded caches when practical. Report any boundary that cannot be enforced.
- Give the nested cluster nonconflicting Pod, Service, and runtime subnets. Store its generated kubeconfig and recorded cluster identity in project-owned disposable state.
- Every project Kubernetes command must use that explicit kubeconfig/context. Every runtime command must use the dedicated endpoint. Do not inherit ambient endpoints or fall back to in-cluster credentials.
- Cleanup must remove only the recorded project-owned kind cluster and its disposable data. Preserve requested redacted failure artifacts.

The operator inside the nested cluster still needs its local service-account credentials and RBAC. The credential prohibition applies to the outer runner's hosting-cluster access.

## Ownership and deliverables

The Kubernetes manager owns provisioning the outer runner, nesting permissions, resource allocation, dedicated runtime storage, network isolation, and access method. Return the runner manifest or CI configuration, tool versions, runtime endpoint location without credentials, storage lifecycle, access instructions, and documented limitations.

The project developer owns `dev/Dockerfile`, `dev/kind.yaml`, runtime and kubeconfig guards, Helm packaging, application images, and `hack/` scripts. The guarded setup/load/integration/E2E/cleanup/full-suite entrypoints now exist and P0 acceptance passed. Helm/controller packaging remains P1 work. Current validation and durable platform requirements are described in [development](development.md) and the [runner profile](../dev/runner-profile.md#durable-mtu-configuration).

## Runner acceptance

Before returning the runner, demonstrate that its dedicated runtime can create a one-node kind cluster with a loopback API, schedule a real Pod, resolve service DNS, bind and use a disposable PVC, complete a Job, and remove the cluster and test data. Record the versions and results without credentials. Validate that the outer runner lacks hosting-cluster credentials and host runtime mounts, and show the configured network boundary without probing the hosting API with credentials.

Once project helpers exist, the developer must additionally prove that poisoned ambient kubeconfigs, remote runtime endpoints, and `USE_EXISTING_CLUSTER=true` are rejected before external requests. Full application acceptance then uses `./hack/test-cluster.sh` for real operator/Core/add-on and browser tests. A successful runner smoke test establishes infrastructure readiness only; application compatibility remains unvalidated.

See [the implementation plan](implementation-plan.md#local-testing-inside-a-container) and [the technology stack decision](tech-stack.md) for the full test scope and architecture.

## Development follow-up: durable MTU correction verified

The P0 application spike passed native Core/client/browser scenarios in the guarded local kind cluster. During reproducible development-image validation, Helm TLS downloads stalled. The outer interface is MTU 1450 while Docker's default bridge and kind networks are 1500. A labelled, disposable 1450 network passed the same HTTPS probe; it was removed. The interactive kind cluster and its node data volume were also removed.

The platform owner corrected the dedicated daemon configuration on 2026-10-03. Both its default bridge and newly created kind network passed the MTU guard against the 1450 runner interface. The development image built and passed all toolchain and browser smoke checks. Fresh complete-suite and injected-failure teardown verification then passed; P0 is complete. Exact Docker settings are in [the runner profile](../dev/runner-profile.md#durable-mtu-configuration), and results are in [development evidence](development.md#coverage-and-evidence). Helpers still reject mismatches; no host-network/TLS bypass or skipped validation was introduced. Project nodes, data volumes and private local state were removed after validation.
