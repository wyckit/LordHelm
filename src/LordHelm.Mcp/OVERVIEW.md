# LordHelm.Mcp

**Purpose.** The MCP server contract. Exposes Lord Helm's capabilities — `dispatch_goal`, `list_skills`, `get_incident`, `list_experts` — to upstream agents. Transport-agnostic: this project defines the surface; a JSON-RPC runtime (e.g. the `ModelContextProtocol` SDK) is wired separately at the host level.

Depends on: `LordHelm.Core`, `LordHelm.Orchestrator`.

## Public types

### Contracts
- `ILordHelmMcpServer` — the MCP contract. Four async methods:
  - `DispatchGoalAsync(DispatchGoalRequest req, ct)` — submits a goal to the Manager.
  - `ListSkillsAsync(ct)` — enumerate available skills.
  - `GetIncidentAsync(incidentId, ct)` — fetch a known incident.
  - `ListExpertsAsync(ct)` — enumerate registered experts.

### DTOs
- `DispatchGoalRequest(Goal, Priority, SessionId?)`.
- `DispatchGoalResponse(GoalId, Accepted, Reason?, DagNodeCount)`.
- `SkillSummary(Id, Version, Env, RiskTier)`.
- `IncidentSummary(IncidentId, SkillId, ExitCode, At, Resolved)`.
- `ExpertSummary(ExpertId, VendorId, Model)`.

### Implementation
- `LordHelmMcpServer(manager, skillsProvider, expertsProvider?)` — default wiring. `skillsProvider` is a `Func<IReadOnlyList<SkillManifest>>` so the server stays decoupled from `ISkillCache` (composition root supplies the lambda). The same shape applies to experts.
  - `DispatchGoalAsync` calls `ILordHelmManager.RunAsync` with a stub executor today (`(stub) executed <id>`). Full production wiring uses `IExpertProvisioner` and is expected to replace the stub when the decomposer is upgraded.
  - `RecordIncident(IncidentSummary)` — call site for the `IncidentResponder` when it wants to surface an incident through the MCP API.

## Collaborators

- **`LordHelm.Orchestrator`** — consumes `ILordHelmManager`.
- **`LordHelm.Skills`** — `skillsProvider` lambda reads from `ISkillCache`.
- **Future:** an `Mcp.Transport` project will translate JSON-RPC requests into calls against `ILordHelmMcpServer`.

## Invariants

1. **Contract stability.** The four method shapes are stable — changing them breaks upstream MCP clients. Extensions go on new methods or new response fields with defaults.
2. **DTOs are immutable records.** No behaviour here; the project is a wire-contract layer.
