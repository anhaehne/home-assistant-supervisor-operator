# Deployment artifacts

This bundle contains the versioned chart, separate CRDs, a rendered example and a local render helper. Application images are embedded in the chart defaults. Published releases use registry digests; unpublished CI builds use full-commit tags and provide image archives in a separate Actions artifact. `images.yaml` in the deployment artifact records those references. Only linux/amd64 on Kubernetes 1.37.0 has been validated. This is the P1 development preview; add-ons, updates and backups/restores require later milestones.

## Helm

Create the installation namespace and an existing credentials Secret with separate random `core-token` and `gateway-token` keys using your secret-management process. Keep credential values out of command history and Git. Set a working StorageClass. Kubernetes schedules Core; for recovery to another node, use storage accessible from that node:

```sh
helm upgrade --install haso ./chart.tgz --namespace home-assistant \
  --set-string storageClassName=YOUR_STORAGE_CLASS \
  --set-string credentialsSecret=credentials --wait --timeout 10m
```

The recommended default `ingress.trustPodNetwork: true` discovers Node Pod CIDRs and configures native HTTP proxy trust. It grants the operator read-only get/list access to Nodes and trusts forwarded client addresses from every Pod in those ranges. If the CNI does not publish authoritative Node CIDRs, or the proxy uses an external/host-network address, set `ingress.trustPodNetwork: false` and supply `ingress.trustedProxies` explicitly. With discovery disabled, `[]` revokes proxy trust and `null` leaves confirmed native settings unmanaged. Avoid simultaneous native HTTP configuration edits while the operator manages this setting.

For chart upgrades, review and apply `crds.yaml` separately first: Helm does not upgrade CRDs from its `crds/` directory. The chart is a singleton installation with fixed admission policy names. Choose one deployment manager; switching between Helm and raw manifests needs an explicit ownership migration.

## kubectl or GitOps

Verify the bundle checksums with `sha256sum -c SHA256SUMS`. Copy `installation-values.example.yaml` to your own values file and replace the placeholders. Regenerate manifests whenever the namespace changes: admission policies and service-account subjects embed that namespace. The checked-in rendered example is illustrative and must not be applied.

```sh
bash ./render-manifests.sh home-assistant ./installation-values.yaml ./rendered
kubectl --context YOUR_CONTEXT apply -f ./rendered/crds.yaml
kubectl --context YOUR_CONTEXT wait --for=condition=Established \
  crd/homeassistantinstances.ha-operator.io crd/homeassistantoperations.ha-operator.io --timeout=60s
kubectl --context YOUR_CONTEXT apply --namespace home-assistant -f ./rendered/manifests.yaml
kubectl --context YOUR_CONTEXT rollout status --namespace home-assistant deployment/supervisor --timeout=600s
```

For GitOps, commit the generated manifests and apply CRDs before custom resources. Provision the namespace and credentials Secret separately. The render helper uses Helm but does not install a Helm release. It omits Helm hooks so the shutdown Job cannot run during a normal apply. GitOps deletion must preserve the `instance-data` PVC and allow the instance finalizer to stop Core gracefully before removing the operator and its RBAC. Do not blindly prune the namespace, PVC, CRDs or admission policies, and never force-delete an unreachable Core Pod to bypass fencing.

Container image artifacts contain `operator.tar.gz` and `gateway.tar.gz`; `gzip -dc FILE | docker load` loads their full-commit tags for local registry import. Published release images are available directly from GHCR. The application images are Kubernetes components, not a supported standalone Compose installation. Pull stock Core using the pinned upstream reference in the chart; it is not repackaged here. GHCR packages must be readable by your cluster (make packages public or supply your usual registry authentication).
