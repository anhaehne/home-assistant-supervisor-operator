# Home Assistant Supervisor Operator

A Kubernetes operator under development, with a built-in Supervisor-compatible API for managing one Home Assistant instance and its compatible add-ons from Home Assistant.

See [the research and implementation plan](docs/implementation-plan.md) for the API scope, architecture, Kubernetes mappings, compatibility limitations, delivery milestones, and an isolated full-cluster kind testing setup with container runner requirements.

See [the technology stack decision](docs/tech-stack.md) for the accepted C#/.NET stack and validated KubeOps framework.

See [the architecture](docs/architecture.md) for the implemented components and boundaries, and [contribution guidance](CONTRIBUTING.md) for the development workflow and required checks.

Status: P0 is complete. The stock Core compatibility spike, development image, fresh full E2E suite and success/failure teardown checks passed in isolated kind on 2026-10-03. See [verification and commands](docs/development.md). HTTP endpoints use ASP.NET Core controllers; P1 adds reconciled Core lifecycle. Add-on lifecycle remains later work.

P1 implements [Core lifecycle and operator foundations](docs/p1-foundation.md): durable instance/operation CRDs, namespace reconciliation, singleton admission, fencing, jobs/logs, retained storage and a development-preview Helm chart. P1 is complete: the final fresh suite passed on 2026-10-04 with 111 unit tests, lifecycle/upgrade/recovery gates and successful teardown, including ownership fencing added during review. [v0.1.0-alpha.1](https://github.com/anhaehne/home-assistant-supervisor-operator/releases/tag/v0.1.0-alpha.1) publishes public images, a Helm chart and a manifest bundle. Anonymous downloads and a fresh installation passed on 2026-10-04; see the deployment artifact and release guides below. Add-on lifecycle, backups and updates remain later milestones.

See [the Kubernetes manager handover](docs/kubernetes-manager-handover.md) for provisioning the isolated development runner. Make and a standalone Python installation are not development prerequisites.

See [development commands and current verification](docs/development.md) and [the initial contract baseline](docs/contract-baseline.md).

```sh
dotnet restore --locked-mode
dotnet test --no-restore
./hack/test-isolation.sh
./hack/test-cluster.sh
```

## Get started with Helm

The `0.1.0-alpha.3` release candidate adds managed reverse-proxy configuration and recommended automatic trust of Kubernetes Pod networks. Core remains unpinned: Kubernetes selects placement using the PVC topology. The tag workflow publishes chart/image artifacts and prepares a draft GitHub prerelease; see the [release procedure](packaging/RELEASING.md) for acceptance and publication status. Cross-node recovery requires storage accessible on the replacement node and verified termination of the previous Core process. The previous `0.1.0-alpha.1` release still requires `selectedNode`.

The `0.1.0-alpha.3` chart uses digest-pinned operator and gateway images. Anonymous download and installation acceptance of the published candidate artifacts must pass before the GitHub prerelease is published; see the release procedure for evidence. You do not need to build images yourself. This is the P1 development preview: only Kubernetes 1.37.0 on linux/amd64 has been validated; add-on management, Core updates and backups/restores remain later work.

You need Helm 3, kubectl, OpenSSL, schedulable Linux amd64 nodes, and a working StorageClass. Your installation account must be able to create CRDs, cluster admission policies, and the Node-read ClusterRole/Binding used by the recommended default. The chart supports one installation per cluster; storage topology determines which nodes can run Core.

Choose your cluster context and StorageClass. Replace the example values below:

```sh
KUBE_CONTEXT=YOUR_CONTEXT
STORAGE_CLASS=YOUR_STORAGE_CLASS

kubectl --context "$KUBE_CONTEXT" get nodes
kubectl --context "$KUBE_CONTEXT" get storageclasses
kubectl --context "$KUBE_CONTEXT" create namespace home-assistant
```

Create the credentials Secret in that namespace. These commands generate separate random tokens in private temporary files and remove those files afterward. If you already manage Secrets externally, provision the same `core-token` and `gateway-token` keys through that system instead.

```sh
(
  set -eu
  umask 077
  token_dir=$(mktemp -d)
  trap 'rm -rf -- "$token_dir"' EXIT
  core_token=$(openssl rand -hex 32)
  gateway_token=$(openssl rand -hex 32)
  printf '%s' "$core_token" > "$token_dir/core-token"
  printf '%s' "$gateway_token" > "$token_dir/gateway-token"
  kubectl --context "$KUBE_CONTEXT" --namespace home-assistant create secret generic credentials \
    --from-file=core-token="$token_dir/core-token" \
    --from-file=gateway-token="$token_dir/gateway-token"
)
```

Install the pinned prerelease, then wait for Core's application health. Helm's readiness check and the instance's `Ready` condition are separate checks:

```sh
helm upgrade --install haso \
  oci://ghcr.io/anhaehne/home-assistant-supervisor-operator/charts/home-assistant-supervisor-operator \
  --version 0.1.0-alpha.3 \
  --kube-context "$KUBE_CONTEXT" --namespace home-assistant \
  --set-string storageClassName="$STORAGE_CLASS" \
  --set-string credentialsSecret=credentials \
  --wait --timeout 10m

kubectl --context "$KUBE_CONTEXT" --namespace home-assistant wait \
  --for=condition=Ready homeassistantinstance/home-assistant --timeout=10m
```

Open a loopback connection to the Home Assistant frontend:

```sh
kubectl --context "$KUBE_CONTEXT" --namespace home-assistant port-forward \
  --address 127.0.0.1 service/core 8123:80
```

Visit <http://127.0.0.1:8123> and complete native onboarding. Keep the port-forward running while using the frontend. Expose the `core` Service for permanent access; the Supervisor and gateway Services are internal APIs. See [the installation guide](docs/p1-foundation.md#installing-the-development-preview) for ingress and GitOps ownership options.

The alpha.3 candidate defaults to `ingress.trustPodNetwork: true`, discovering and trusting all Kubernetes node Pod CIDRs for forwarded requests. This requires read-only node access and grants every Pod in those ranges permission to supply client addresses. Node CIDRs must be authoritative for your CNI and cover the ingress peer addresses; external or host-network proxies require explicit trusted addresses. The new default and managed native HTTP updates on existing PVCs passed isolated acceptance; alpha.2 does not support these settings. See [proxy configuration and upgrades](docs/p1-foundation.md#trusted-ingress-proxies) for requirements and retained PVCs. To limit trust to explicit ingress proxy addresses instead, disable discovery:

```yaml
ingress:
  trustPodNetwork: false
  trustedProxies:
    - 10.0.0.5/32
```

To uninstall, use `helm --kube-context "$KUBE_CONTEXT" uninstall haso --namespace home-assistant --wait --timeout 10m`. The shutdown hook stops Core gracefully and retains the `instance-data` PVC. Keep the namespace and credentials Secret if you intend to reinstall with the same release and storage. For chart upgrades, review and apply both updated instance and operation CRDs separately first; Helm does not upgrade CRDs from the chart's `crds/` directory. See [the artifact guide](packaging/README.md#helm) for details.

## Deployment artifacts

[Deployment artifacts](.github/workflows/deployment-artifacts.yml) runs on pull requests, pushes to `main`, version tags and manual dispatch. It restores locked dependencies, runs unit tests, builds linux/amd64 operator/gateway images, and uploads image archives, a versioned Helm chart and a kubectl/GitOps manifest bundle with SHA-256 checksums. Download `deployment-VERSION` and `container-images-VERSION` from the Actions run. Installation and manifest rendering instructions are included in the bundle and in [the artifact guide](packaging/README.md).

A `vSEMVER` tag (for example `v0.1.0` or `v0.1.0-rc.1`) additionally publishes images under `ghcr.io/OWNER/REPOSITORY/operator` and `/gateway`, and the Helm chart under `oci://ghcr.io/OWNER/REPOSITORY/charts/home-assistant-supervisor-operator`. Release chart defaults use the pushed image digests. After publication succeeds, the workflow creates a draft GitHub release with the chart, manifest bundle, image references and checksums; prerelease tags are marked as prereleases. Published releases are never overwritten. The workflow uses the repository's `GITHUB_TOKEN`; configure GHCR package visibility or cluster registry authentication as appropriate. PR, branch and manual non-tag builds only upload artifacts. Unpublished charts use full-commit image tags supplied by their accompanying archives.

The artifact workflow validates unit tests and packaging; it does not replace the full isolated kind/native browser/P1 acceptance release gate. Complete that gate for the application source being released, and verify packaging-only changes with the published-artifact installation check. Only the P1 development preview on Kubernetes 1.37.0 and linux/amd64 is validated. Namespace, storage and credentials remain installation-specific in current source. Helm hooks are omitted from raw manifests; GitOps removal must preserve the PVC and complete graceful finalization.

See [the release procedure](packaging/RELEASING.md) for acceptance, immutable tags and public installation checks.
