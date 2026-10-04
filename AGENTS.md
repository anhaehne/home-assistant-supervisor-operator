# Development instructions

Resolve development setup problems at their source with a correct, durable fix. Do not bypass failed prerequisites, weaken isolation, silently substitute tooling, or skip required checks to make progress. If a durable fix needs platform access or a user decision, report the concrete issue and escalate it to the user. This preference applies to future sessions in this repository.

Follow the accepted C#/.NET stack and milestones in `docs/implementation-plan.md` and `docs/tech-stack.md`. Never use the Kubernetes cluster hosting the development container or its runtime sockets for project tests. Use only the dedicated nested runtime and an explicitly verified project-owned kind cluster.

Preserve existing user edits. Report actual verification results and distinguish the development spike from implemented operator or Home Assistant compatibility.

Keep the dedicated daemon's default bridge and new bridge-network MTUs no greater than the runner interface. The platform corrected the earlier 1500/1450 mismatch; fresh network creation and development-image validation passed on 2026-10-03. See `dev/runner-profile.md`. Never bypass the MTU guard. Local API runs must not opt into ambient hosting-cluster credentials; only the installed workload explicitly enables its namespace-scoped metrics adapter.

Expect the dedicated Docker sidecar to restart and continue autonomously. Use `hack/recover-runtime.sh`: revalidate the fixed endpoint, MTU, exact sealed node IDs/names/labels and kubeconfig before resuming retained nodes. If a replacement daemon has lost all recorded nodes, archive the interrupted private state and rerun a fresh suite. Never adopt changed or partial nodes, contact an old API, claim lost data was retained, or count interrupted tests as successful cleanup. Do not require repeated user confirmation for this expected recovery.

## Subagents and parallel work

Use subagents proactively when independent work can usefully run in parallel, such as investigating separate components, implementing changes in different files, or reviewing completed code while independent verification runs. Do not delegate trivial edits or dependent steps merely to create parallel activity.

- Give each subagent a bounded task, the relevant context and constraints, explicit file ownership when editing, and a clear expected result. All agents must follow these development instructions.
- The primary agent owns coordination, integration, final verification and communication with the user. Continue useful independent work while subagents run, inspect their results, and resolve disagreements against the code and test evidence.
- Agents share the workspace. Avoid concurrent edits to the same files, preserve user changes, and coordinate before changing shared contracts. Assign commits, branch changes, tags and pushes to the primary agent unless explicitly delegated.
- Keep project cluster creation, fault injection, recovery, teardown and acceptance suites serial under one designated owner. Do not run competing workflows against the dedicated daemon, `.dev-state`, or shared build outputs. Read-only code review can run alongside testing; runtime inspection must still respect the existing guards and locks.
- Use a separate subagent for requested code review. Keep review read-only, fix actionable P3-or-higher findings, then restart review against the updated changes until no such findings remain. Report actual verification evidence; review does not replace required tests.
