# Development instructions

Resolve development setup problems at their source with a correct, durable fix. Do not bypass failed prerequisites, weaken isolation, silently substitute tooling, or skip required checks to make progress. If a durable fix needs platform access or a user decision, report the concrete issue and escalate it to the user. This preference applies to future sessions in this repository.

Follow the accepted C#/.NET stack and milestones in `docs/implementation-plan.md` and `docs/tech-stack.md`. Never use the Kubernetes cluster hosting the development container or its runtime sockets for project tests. Use only the dedicated nested runtime and an explicitly verified project-owned kind cluster.

Preserve existing user edits. Report actual verification results and distinguish the development spike from implemented operator or Home Assistant compatibility.

Keep the dedicated daemon's default bridge and new bridge-network MTUs no greater than the runner interface. The platform corrected the earlier 1500/1450 mismatch; fresh network creation and development-image validation passed on 2026-10-03. See `dev/runner-profile.md`. Never bypass the MTU guard. Local API runs must not opt into ambient hosting-cluster credentials; only the installed workload explicitly enables its namespace-scoped metrics adapter.
