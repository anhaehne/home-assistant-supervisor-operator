# Release procedure

Use Semantic Versioning and start with `v0.1.0-alpha.1` while only the P1 preview is complete. Use a new tag for every correction; never move or reuse a published tag.

1. Complete the full isolated P1 suite for the application source being released, with successful teardown. Packaging-only changes require packaging checks and the published-artifact installation check below.
2. Commit and push the release changes. Wait for the Deployment artifacts workflow on that commit to pass.
3. Create an annotated tag on the accepted commit and push it:

   ```sh
   git tag -a v0.1.0-alpha.1 ACCEPTED_COMMIT -m 'P1 development preview v0.1.0-alpha.1'
   git push origin v0.1.0-alpha.1
   ```

4. The tag workflow runs unit tests, builds and publishes operator/gateway images and the Helm chart, verifies the manifest bundle and creates a draft GitHub prerelease with checksummed assets. Image references in the chart are pinned by digest.
5. For anonymous installation, make the repository and each GHCR package public: operator, gateway, and charts/home-assistant-supervisor-operator. Package visibility is managed separately from repository visibility.
6. Download the chart and pull both images without registry credentials. Verify release asset checksums, the chart version and digest defaults, and the rendered manifest bundle. Install the published chart in a fresh guarded project-owned kind cluster with configured node, storage and credentials. Confirm successful reconciliation and application health, then teardown and verify cleanup.
7. Replace the draft's pending acceptance paragraph with the actual verification evidence and limitations. Publish it as a prerelease. Enable GitHub immutable releases before publishing to protect the tag and release assets.

A green artifact workflow alone does not prove that anonymous downloads or application installation work. Do not publish the draft while those checks remain blocked. Never rerun a successful tag build to replace its image or chart contents; create a new version instead.
