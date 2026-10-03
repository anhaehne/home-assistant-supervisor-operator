# Home Assistant Supervisor Operator

A Kubernetes operator under development, with a built-in Supervisor-compatible API for managing one Home Assistant instance and its compatible add-ons from Home Assistant.

See [the research and implementation plan](docs/implementation-plan.md) for the API scope, architecture, Kubernetes mappings, compatibility limitations, delivery milestones, and an isolated full-cluster kind testing setup with container runner requirements.

See [the technology stack decision](docs/tech-stack.md) for the accepted C#/.NET stack and provisional KubeOps framework.

See [the architecture](docs/architecture.md) for the implemented components and boundaries, and [contribution guidance](CONTRIBUTING.md) for the development workflow and required checks.

Status: P0 is complete. The stock Core compatibility spike, development image, fresh full E2E suite and success/failure teardown checks passed in isolated kind on 2026-10-03. See [verification and commands](docs/development.md). HTTP endpoints use ASP.NET Core controllers; Kubernetes reconciliation and add-on lifecycle remain later milestones.

Deployment currently uses guarded development scripts and `deploy/p0.yaml` in a disposable kind cluster. There is no user-installable Helm chart or published application release yet. P1 adds operator reconciliation, Core lifecycle and Helm packaging.

See [the Kubernetes manager handover](docs/kubernetes-manager-handover.md) for provisioning the isolated development runner. Make and a standalone Python installation are not development prerequisites.

See [development commands and current verification](docs/development.md) and [the initial contract baseline](docs/contract-baseline.md).

```sh
dotnet restore --locked-mode
dotnet test --no-restore
./hack/test-isolation.sh
./hack/test-cluster.sh
```
