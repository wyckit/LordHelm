# LordHelm.Orchestrator

**Purpose.** The Manager agent (Lord Helm), goal decomposition, expert provisioning, the dataflow bus (blackboard pattern), and the default `IEngramClient` implementation.

Depends on: `LordHelm.Core`, `LordHelm.Skills`, `LordHelm.Execution`, `LordHelm.Providers`, `LordHelm.Monitor`, `McpEngramMemory.Core`, `Microsoft.Extensions.Logging.Abstractions`.

## Flow

```
user goal  ──►  LordHelmMcpServer.DispatchGoalAsync
                      │
                      ▼
                 ILordHelmManager.RunAsync(goal, skills, executeTaskFunc, ct)
                      │
                      ▼
         IGoalDecomposer.DecomposeAsync(goal, skills)      ─► TaskNode[]  (default: 1-node passthrough)
                      │
                      ▼
         TaskDag.TopoSort(nodes)                           ─► ordered IReadOnlyList<TaskNode>
                      │
                      ▼
         for each node in topo order:
             executeTaskFunc(node)   ── in production: IExpertProvisioner.ProvisionAsync → ExpertRunner → IExecutionRouter
                      │
                      ▼
             ManagerResult { Dag, NodeOutputs[], Succeeded, ErrorDetail }

in parallel:  DataflowBus.PublishAsync(NodeEvent)  ──►  subscribers (idempotent dispatch)
```

## Public types

### Engram facade
- `EngramClient` implements `IEngramClient` (defined in `LordHelm.Core`). Current default is an in-process buffer with Jaccard search so the system is runnable without a live engram server. Swap for a real MCP-wired client at composition time.

### DAG + Manager
- `TaskNode(Id, Goal, DependsOn[])` — a unit of work in the goal tree.
- `TaskDag.TopoSort(nodes)` — Kahn's algorithm. Throws `InvalidOperationException` on a cycle.
- `ManagerResult(Goal, Dag, NodeOutputs, Succeeded, ErrorDetail)` — returned by a full goal run.
- `ILordHelmManager.RunAsync(goal, skills, executeTaskFunc, ct)` — Decompose → topo-sort → execute in dependency order, collecting outputs.
- `LordHelmManager` — default implementation.

### Goal decomposition
- `IGoalDecomposer.DecomposeAsync(goal, skills, ct)` — breaks a natural-language goal into a `TaskNode[]` dependency tree.
- `PassthroughGoalDecomposer` — development fallback that emits a single-node DAG. An LLM-backed decomposer that consumes `IProviderOrchestrator` is the next step.

### Expert provisioning
- `ProvisionRequest(ExpertId, SkillId, CliVendorId, Model, Goal, ArgsJson?)`.
- `ProvisionedExpert(ExpertProfile, ExpertRunner)` — the `ExpertRunner` is a `Func<CancellationToken, Task<string>>` closure that calls `IExecutionRouter.RouteAsync` with the resolved skill + args.
- `IExpertProvisioner.ProvisionAsync(req, ct)` — resolves the skill from `ISkillCache`, composes the runner. Returns `null` if the skill is unknown.
- `DefaultExpertProvisioner` — default implementation; default shell comes from `OperatingSystem.IsWindows()`.

### Dataflow bus
- `NodeRef(Namespace, Id, Metadata)`, `NodeEvent(Node, Text, At)`, `SubscriptionSpec(Id, Namespace, IdPattern, MetadataPredicate?)`.
- `IDataflowBus.SubscribeAsync`, `PublishAsync`, `UnsubscribeAsync`.
- `DataflowBus` — in-memory blackboard, glob-matched id patterns, idempotent dispatch keyed by `(subscriptionId, namespace, id, metadataHash)`.

## Collaborators

- **`LordHelm.Execution`** — `DefaultExpertProvisioner` composes the `ExpertRunner` closure around `IExecutionRouter`.
- **`LordHelm.Skills`** — `DefaultExpertProvisioner` resolves skills from `ISkillCache`.
- **`LordHelm.Consensus`** — `IncidentResponder` uses `IEngramClient` (defined in `LordHelm.Core`, implemented here) to persist incidents and resolutions.
- **`LordHelm.Mcp`** — `LordHelmMcpServer` delegates to `ILordHelmManager` for `dispatch_goal` requests.
- **`LordHelm.Web`** — `Program.cs` registers every type in this project as a singleton.

## Invariants

1. **`ILordHelmManager` never executes tasks itself.** It always delegates through the `executeTaskFunc` parameter (in production: the `ExpertRunner` from `DefaultExpertProvisioner`). The Manager is pure orchestration.
2. **DAG cycles are rejected at topological-sort time**, never silently ignored.
3. **`DataflowBus` dispatch is idempotent.** Re-publishing the same `NodeEvent` fires the handler once per (subscription, node) pair.
