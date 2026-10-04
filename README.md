# Home Assistant Supervisor Operator

A Kubernetes operator under development, with a built-in Supervisor-compatible API for managing one Home Assistant instance and its compatible add-ons from Home Assistant.

See [the research and implementation plan](docs/implementation-plan.md) for the API scope, architecture, Kubernetes mappings, compatibility limitations, delivery milestones, and an isolated full-cluster kind testing setup with container runner requirements.

See [the technology stack decision](docs/tech-stack.md) for the accepted C#/.NET stack and validated KubeOps framework.

See [the architecture](docs/architecture.md) for the implemented components and boundaries, and [contribution guidance](CONTRIBUTING.md) for the development workflow and required checks.

Status: P0 is complete. The stock Core compatibility spike, development image, fresh full E2E suite and success/failure teardown checks passed in isolated kind on 2026-10-03. See [verification and commands](docs/development.md). HTTP endpoints use ASP.NET Core controllers; P1 adds reconciled Core lifecycle. Add-on lifecycle remains later work.

P1 implements [Core lifecycle and operator foundations](docs/p1-foundation.md): durable instance/operation CRDs, namespace reconciliation, singleton admission, fencing, jobs/logs, retained storage and a development-preview Helm chart. P1 is complete: the final fresh suite passed on 2026-10-04 with 111 unit tests, lifecycle/upgrade/recovery gates and successful teardown, including ownership fencing added during review. Tagged prereleases publish application images and the chart; see the deployment artifact and release guides below. Add-on lifecycle, backups and updates remain later milestones.

See [the Kubernetes manager handover](docs/kubernetes-manager-handover.md) for provisioning the isolated development runner. Make and a standalone Python installation are not development prerequisites.

See [development commands and current verification](docs/development.md) and [the initial contract baseline](docs/contract-baseline.md).

```sh
dotnet restore --locked-mode
dotnet test --no-restore
./hack/test-isolation.sh
./hack/test-cluster.sh
```

## Deployment artifacts

[Deployment artifacts](.github/workflows/deployment-artifacts.yml) runs on pull requests, pushes to `main`, version tags and manual dispatch. It restores locked dependencies, runs unit tests, builds linux/amd64 operator/gateway images, and uploads image archives, a versioned Helm chart and a kubectl/GitOps manifest bundle with SHA-256 checksums. Download `deployment-VERSION` and `container-images-VERSION` from the Actions run. Installation and manifest rendering instructions are included in the bundle and in [the artifact guide](packaging/README.md).

A `vSEMVER` tag (for example `v0.1.0` or `v0.1.0-rc.1`) additionally publishes images under `ghcr.io/OWNER/REPOSITORY/operator` and `/gateway`, and the Helm chart under `oci://ghcr.io/OWNER/REPOSITORY/charts/home-assistant-supervisor-operator`. Release chart defaults use the pushed image digests. After publication succeeds, the workflow creates a draft GitHub release with the chart, manifest bundle, image references and checksums; prerelease tags are marked as prereleases. Published releases are never overwritten. The workflow uses the repository's `GITHUB_TOKEN`; configure GHCR package visibility or cluster registry authentication as appropriate. PR, branch and manual non-tag builds only upload artifacts. Unpublished charts use full-commit image tags supplied by their accompanying archives.

The artifact workflow validates unit tests and packaging; it does not replace the full isolated kind/native browser/P1 acceptance release gate. Complete that gate for the exact source revision before tagging a release. Only the P1 development preview on Kubernetes 1.37.0 and linux/amd64 is validated. Namespace, selected node, storage and credentials remain installation-specific. Helm hooks are omitted from raw manifests; GitOps removal must preserve the PVC and complete graceful finalization.

See [the release procedure](packaging/RELEASING.md) for acceptance, immutable tags and public installation checks.
