# Release procedure

Use Semantic Versioning. The next P1 preview is `v0.1.0-alpha.2`; `v0.1.0-alpha.1` remains unchanged. Use a new tag for every correction; never move or reuse a published tag.

1. Complete the full isolated P1 suite for the application source being released, with successful teardown. Packaging-only changes require packaging checks and the published-artifact installation check below.
2. Commit and push the release changes. Wait for the Deployment artifacts workflow on that commit to pass.
3. Create an annotated tag on the accepted commit and push it:

   ```sh
   git tag -a v0.1.0-alpha.2 ACCEPTED_COMMIT -m 'P1 scheduler placement preview v0.1.0-alpha.2'
   git push origin v0.1.0-alpha.2
   ```

4. The tag workflow runs unit tests, builds and publishes operator/gateway images and the Helm chart, verifies the manifest bundle and creates a draft GitHub prerelease with checksummed assets. Image references in the chart are pinned by digest.
5. For anonymous installation, make the repository and each GHCR package public: operator, gateway, and charts/home-assistant-supervisor-operator. Package visibility is managed separately from repository visibility.
6. Download the chart and pull both images without registry credentials. Verify release asset checksums, the chart version and digest defaults, and the rendered manifest bundle. Install the published chart in a fresh guarded project-owned kind cluster with configured storage and credentials. Confirm successful reconciliation and application health. Follow HTTP redirects when checking the fresh frontend (for example, `curl --fail --location` through a loopback port-forward), because initial onboarding redirects can have an empty body. Gracefully uninstall, confirm the same PVC is retained, then teardown and verify cleanup.
7. Replace the draft's pending acceptance paragraph with the actual verification evidence and limitations. Publish it as a prerelease. Enable GitHub immutable releases before publishing to protect the tag and release assets.

A green artifact workflow alone does not prove that anonymous downloads or application installation work. Do not publish the draft while those checks remain blocked. Never rerun a successful tag build to replace its image or chart contents; create a new version instead.

## First prerelease evidence

[v0.1.0-alpha.1](https://github.com/anhaehne/home-assistant-supervisor-operator/releases/tag/v0.1.0-alpha.1) targets commit `0603633a49f4bd0ff2374908c2d9ed44ce898fa3`. Its [tag workflow](https://github.com/anhaehne/home-assistant-supervisor-operator/actions/runs/37214582544) passed on 2026-10-04, including 111 unit tests. The application source had already passed the full P1 suite in `.test-artifacts/p0.jXSyWT`; intervening changes concerned packaging and release automation.

The repository is public and immutable releases are enabled. Chart and both digest-pinned images downloaded anonymously with empty credential configurations. All release asset checksums and internal manifest bundle checksums passed, and the GHCR chart matched the release attachment byte-for-byte. The bundle rendered successfully for a separate namespace.

The published chart installed in a fresh guarded two-node Kubernetes 1.37.0 kind cluster without application image overrides or registry credentials. Core and gateway were Ready with zero restarts, instance reconciliation reported Succeeded, and Core's web interface responded. Graceful uninstall retained the instance-data PVC; teardown verified both nodes and their volumes were absent and removed local state. Local evidence is in `.test-artifacts/release-alpha-1` and `.test-artifacts/release-alpha-1-install.log`. No runner interruption occurred.

## Second prerelease candidate

`v0.1.0-alpha.2` targets commit `48020165c5da84b1482ee3ce0994439244308116`. The [main workflow](https://github.com/anhaehne/home-assistant-supervisor-operator/actions/runs/37218975943) and [tag workflow](https://github.com/anhaehne/home-assistant-supervisor-operator/actions/runs/37219112249) passed on 2026-10-04. Application acceptance is `.test-artifacts/p0.VS5OM8/`: 112 unit tests, all P0/P1 gates including legacy selector removal and cross-node recovery, and complete teardown. The separate injected-failure cleanup wrapper also passed.

The alpha.2 chart and both digest-pinned application images downloaded anonymously. Release asset and internal bundle checksums passed, and the registry chart matched its release attachment byte-for-byte. A fresh guarded two-node kind installation used chart image defaults without node or application image overrides; instance reconciliation and socket health succeeded, and the native frontend responded after following its onboarding redirect. Graceful uninstall retained the same bound PVC. Cleanup removed both nodes, node data volumes, the shared test volume and local state. Evidence is `.test-artifacts/release-alpha-2/result.json` (exit zero, complete teardown) and `.test-artifacts/release-alpha-2-install-rerun.log`. The earlier `.test-artifacts/release-alpha-2-first-check/` failed because the frontend check did not follow redirects; it cleaned up and does not count as acceptance.

The GitHub prerelease remains a draft ready for publication review; the chart and images are available from GHCR. Cross-node acceptance uses shared test storage and does not validate production CSI attachment, hardware or LAN portability. The P1 preview still excludes add-on lifecycle, backups and Core updates.
