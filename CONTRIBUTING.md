# Contributing

Read [AGENTS.md](AGENTS.md), the [architecture](docs/architecture.md), [implementation plan](docs/implementation-plan.md) and [technology decision](docs/tech-stack.md) before changing behavior. Follow the accepted C#/.NET stack and the current milestone. Preserve existing contributor edits and keep claims limited to implemented, verified behavior.

## Development environment

Use the SDK pinned in `global.json` and the repository's NuGet lock files. The [development guide](docs/development.md) lists pinned tools and commands; the [runner profile](dev/runner-profile.md) defines the dedicated nested-runtime prerequisites. Make and standalone Python are not prerequisites. Native Python checks run inside stock Core.

Fix development setup failures at their source. Do not skip prerequisites, change to an ambient cluster, weaken isolation or silently substitute tooling. If repair needs platform access or a decision, report the concrete failure and escalate it. The daemon's default bridge and new bridge-network MTUs must fit the runner interface. Never bypass that guard.

Never use the Kubernetes cluster hosting the development container or its runtime sockets. Cluster tests require the provisioned dedicated Docker socket and the explicitly verified project-owned kind cluster. Do not enable the installed workload's in-cluster metrics credentials when running the API locally.

## Implementing changes

Use ASP.NET Core controllers for HTTP endpoints in both API services. Keep route attributes, status codes and wire contracts explicit; use constructor injection for services and pass request cancellation through. Keep persistence, metrics and socket transport separate from controller actions. Authentication and common error handling belong in the configured middleware. Preserve snake_case names, nulls, native aliases and success/error envelopes; do not introduce MVC default validation responses where the native contract requires a different envelope.

Retain the gateway's fixed socket reads and separate credential boundary. Do not turn it into a generic proxy or expose caller-selected commands or URLs. Unsupported capabilities must fail honestly. Do not fabricate HAOS identity, measurements or successful operations to satisfy the UI.

Commit source, manifests, documentation, SDK/package pins and `contracts/baseline.json`. Do not commit `.dev-state/`, kubeconfigs, tokens, browser account data, `.test-artifacts/`, build output or tool caches. Use the diagnostic collector for redacted evidence; do not export Secrets, environment dumps or browser storage.

For a deliberate dependency or project-reference change, update the affected lock files, review the dependency changes, then confirm `dotnet restore --locked-mode`. For an upstream compatibility change, update the agreed release/digest/checksum pins, inspect inventory drift, regenerate with `./hack/refresh-contracts.sh --write` in a guarded deployed cluster, and review the baseline diff. Normal verification uses `--check`; refreshing the baseline is not a way to dismiss unexplained failures.

## Checking changes

Run fast checks from the repository root:

```sh
dotnet restore --locked-mode
dotnet test --no-restore
./hack/test-isolation.sh
git diff --check
```

Add meaningful regression tests for changed contracts, state transitions or permission boundaries. Avoid tests that merely mirror implementation. An HTTP/controller refactor must retain the wire behavior and pass the native suite, not just compile.

Run the full fresh suite for application, contract, manifest or integration changes:

```sh
./hack/test-cluster.sh
```

This command refuses existing local state, creates its own cluster, loads pinned images, runs infrastructure/native client/browser checks, collects diagnostics and tears down on success or failure. Exit zero requires successful cleanup. The test cluster contains one Core installation; replacement and migration scenarios are sequential.

For changes to setup, cleanup or the development image, also run the applicable acceptance gates serially:

```sh
./hack/install-buildx.sh
./hack/dev-up.sh
./hack/build-dev-image.sh
./hack/dev-down.sh
./hack/test-teardown.sh
./hack/test-cluster.sh
```

The teardown regression deliberately makes the inner suite exit 41 after real setup; its wrapper succeeds only when that expected failure and cleanup are verified. Development-image smoke tests check tools and Chromium/Firefox/WebKit dependencies; stock Core behavior is exercised by the separate E2E suite.

For interactive work, use the sequence in [development](docs/development.md). Inspect an existing recorded cluster before removing it with `dev-down.sh`. If cleanup fails, retain the recorded state for diagnosis and fix the underlying issue; do not delete the identity record to conceal leftover resources. In a managed runner, use its supported approval/escalation mechanism for runtime, network, .NET IPC and browser access.

## Submitting work

Describe the concrete problem, resulting behavior, relevant boundary changes and actual verification results. Include the command, exit status and private artifact location when useful, without copying credentials. Distinguish a passing spike from production operator support, and identify any required check that remains blocked. Update architecture or contribution guidance when the workflow changes, and the implementation plan when a milestone is actually completed. A green P0 run does not establish later controller, add-on, backup or hardware milestones.
