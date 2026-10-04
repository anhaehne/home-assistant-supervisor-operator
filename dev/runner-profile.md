# Dedicated nested-runtime runner profile

The development image supplies tools and browsers. It does not supply nesting privileges, a daemon, resource reservations, or network isolation. Provision those separately; never mount a node's Docker/containerd socket or point the helpers at the hosting Kubernetes API.

The validated managed runner exposes a dedicated Docker 29.8.1 daemon at `/run/paseo-docker/docker.sock`. Its socket is shared with this development container through the runner Pod's temporary volume; the daemon has its own disposable storage. The runtime and generated kind node identities are sealed by DevGuard. Kubernetes 1.37.0 is the initial tested target, with the node digest in `kind.yaml`. This is an amd64 profile; other architectures require their own acceptance run.

For a new runner, the platform owner must provide:

| Setting | Required configuration |
| --- | --- |
| Development container | Built `dev/Dockerfile`; writable project volume; ordinary unprivileged process; no host network/PID namespaces |
| Runtime | Dedicated daemon and data directory; socket at the path above; no node runtime mounts; daemon lifecycle tied to the runner |
| Credentials | `automountServiceAccountToken: false`; no hosting kubeconfigs or cloud credentials |
| Nesting | Prefer rootless only after user namespaces, cgroup v2 delegation and networking are demonstrated; otherwise explicitly provision the dedicated daemon's nesting privileges |
| Resources | Initial CPU/memory estimates are 4 CPUs and 8 GiB RAM; dedicated Docker now has a 200 GiB persistent PVC. The two-node suite with development-image caches measured 21 GB used after loading; the former 20 GiB emptyDir was insufficient. Monitor usage and cache growth |
| Network | Loopback API/UI access; deny hosting API/node management/cloud metadata routes, including IPv6 where enabled; permit required DNS and dependency/image downloads |
| MTU | Set the daemon's default bridge MTU and new bridge-network MTU no greater than the outer runner interface; this runner needs 1450 |
| Storage | Project files retained separately; kind nodes, Core PVCs, operator spike state and credentials disposable; verified image caches may remain |
| Acceptance | Run infrastructure smoke, isolation regressions, then `hack/test-cluster.sh`; publish results and redacted artifacts |

The currently supplied development container has 2 CPUs and 4 GiB RAM. The dedicated daemon reports a larger resource view, which does not establish reservations. Platform network enforcement and reservations require confirmation from the platform owner; project guards establish configuration isolation, not a hosting-network firewall. See the [platform handover](../docs/kubernetes-manager-handover.md).

Build the image from an already provisioned guarded runner using `./hack/build-dev-image.sh`; this validates its tools and Chromium/Firefox/WebKit launch, rendering, JavaScript and clicks without network access at runtime. Image construction downloads checksum-pinned tool binaries and uses an immutable Playwright/.NET base. Only the Docker client is installed; the image runs as `pwuser`. The base includes SDK 10.0.401 and Playwright 1.63.0 browser dependencies. NuGet packages are restored from repository lock files; the image smoke runner is published from that same pinned package.

The full suite owns a fresh kind cluster, one Core installation, private credentials and loopback forwards. It collects redacted diagnostics and tears down on success, failure and handled signals. `HASO_TEST_FAIL_AFTER_SETUP=1 ./hack/test-cluster.sh` deliberately returns 41 and must still remove its cluster/state. An existing interactive cluster is refused; inspect it and use `dev-down.sh` first.

## Durable MTU configuration

On 2026-10-03, the runner interface reported MTU 1450 while the dedicated daemon's `bridge` and `kind` networks reported 1500. The pinned Helm HTTPS download stalled during TLS on 1500 and succeeded on a labelled, temporary 1450 network; that diagnostic network was removed. The platform owner corrected the daemon configuration. The default bridge and newly created kind network passed the MTU guard, and the development image built and passed toolchain plus Chromium/Firefox/WebKit checks. Helpers continue to reject mismatches instead of changing TLS, using host networking, or skipping image validation.

For provisioning or repair, the platform owner must merge these settings into the dedicated daemon configuration and restart it, then recreate unused networks. Do not replace other daemon settings or modify networks belonging to running clusters. Docker documents separate [default bridge and new network options](https://docs.docker.com/reference/cli/dockerd/):

```json
{
  "mtu": 1450,
  "default-network-opts": {
    "bridge": {"com.docker.network.driver.mtu": "1450"}
  }
}
```

After provisioning or repair, run a fresh guarded cluster, build/validate the development image, verify injected-failure teardown, and run the complete suite. Daemon configuration belongs to the platform; repository helpers verify it on each run.

## Persistent dedicated-runtime storage

On 2026-10-04, the platform owner confirmed the two runner interruptions were kubelet evictions after Docker exceeded its former 20 GiB emptyDir limit. Dedicated Docker storage now uses the 200 GiB ReadWriteOnce Longhorn PVC `paseo-docker-data`, mounted at `/var/lib/docker`, with three healthy replicas. See the [platform resolution](../docs/kubernetes-manager-handover.md#platform-resolution-persistent-docker-storage). This preserves Docker data across outer Pod replacement, but does not preserve running test or agent processes.

Check usage with `docker --host unix:///run/paseo-docker/docker.sock system df`. The full suite removes only its sealed project nodes and recorded data volumes; verified image caches may remain. Never prune unrelated containers, volumes or images to recover space. Use the runtime recovery helper before resuming retained nodes or starting a fresh suite after an interruption.
