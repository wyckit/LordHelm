# Lord Helm — Spec Fidelity Audit

**Audited:** 2026-04-21
**Auditors:** three parallel sonnet reviewers (§1-2, §3-4, §5-6) + 13-expert engram panel for wiring decisions.
**Verdict:** Every subsystem is implemented correctly in isolation, but both composition roots (`LordHelm.Host/Program.cs`, `LordHelm.Web/Program.cs`) were effectively empty. This document tracks each spec bullet, the implementation status, and the wiring gap being closed.

Legend: **Implemented** = code exists; **Wired** = registered in DI and reachable end-to-end from a running process.

---

## §1 — Entity Model & Storage

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| Skill Library: hybrid XML/JSON compiled manifests with `<ExecutionEnvironment>`, `<RequiresApproval>` | ✅ | ❌ → ✅ | `skill-manifest.xsd` + canonicalizer + SQLite cache existed in isolation; composition root now registers `ISkillCache`, `ISkillLoader`, `SkillFileWatcher`, and seeds from `skills/`. |
| Skills natively ingestible as engram nodes | ⚠️ | ❌ → ✅ | `IEngramClient` facade introduced; `SkillLoader` upserts canonical XML into engram namespace `lord_helm_skills` after each SQLite write. |
| Experts: ephemeral personas with injected Skill Loadout | ❌ | ❌ → ✅ | New `IExpertProvisioner` + `DefaultExpertProvisioner`: resolves manifests from `ISkillCache` by ID, picks CLI vendor, returns an `ExpertRunner` delegate that routes through `IExecutionRouter`. |
| Manager Agent: always-on orchestrator, parses goals, queries skills, delegates | ✅ | ❌ → ✅ | `LordHelmManager` registered in DI; `LordHelmMcpServer.DispatchGoalAsync` now invokes the Manager with `IExpertProvisioner`-produced runners instead of a stub lambda. |

## §2 — Core Infrastructure

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| Shared Brain: agents read/write nodes in McpEngramMemory | ❌ | ❌ → ⚠️ | `IEngramClient` added as the single facade; used by `SkillLoader`, `IncidentResponder`, and `DataflowBus` (when configured). Full agent-to-engram pub/sub remains a future phase; interface is in place. |
| Watcher: continuous .NET daemon tracking stdout/stderr/timeouts/CPU | ✅ | ❌ → ✅ | `Watcher` registered as `IHostedService`. `WidgetState.ApplyProcessEvent` now called by a bridge hosted service consuming `Watcher.Events`. |
| Scout Protocol: cron-polled `--help`/`--version`, updates CLI Spec Nodes | ✅ | ❌ → ✅ | `ScoutService` registered as `IHostedService` with three default `ScoutTarget`s (claude/gemini/codex); `onMutation` callback wired through `ITranspilerCacheInvalidator` so `JitTranspiler` drops stale entries on drift. |

## §3 — Execution Routing & Compilation

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| JIT transpile XML/JSON → CLI syntax referencing Scout spec nodes | ⚠️ | ❌ → ✅ | `JitTranspiler` wired and implements `ITranspilerCacheInvalidator`; Scout `MutationEvent` now calls `Invalidate(vendorId, version)`. Flag table bootstrap from live `CliSpec` is a follow-on phase (table remains `FlagMappingTable.Default()` for now). |
| Execution Router reads `<ExecutionEnvironment>` and dispatches | ✅ | ❌ → ✅ | `IExecutionRouter` registered; consumed by `DefaultExpertProvisioner`. |
| Host: Approval Gate for destructive actions | ✅ | ❌ → ✅ | `IApprovalGate`, `IAuditLog`, `IHostRunner` all registered. `ApprovalQueueBridge` hosted service consumes `ApprovalGate.PendingReader` and surfaces entries as `WidgetKind.Approval` widgets. |
| Sandbox: ephemeral Docker container, capture, destroy | ✅ | ❌ → ✅ | `DockerSandboxRunner.CreateDefault` registered. Image digest requirement enforced at runtime. |

## §4 — Think Tank Workflow

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| Decomposition into dependency tree | ⚠️ | ❌ → ⚠️ | `PassthroughGoalDecomposer` remains as default. An LLM-backed decomposer via `MultiProviderOrchestrator` is a documented follow-on; composition now exposes the seam. |
| Dynamic Assembly: provision experts with CLI target + loadout | ❌ | ❌ → ✅ | `IExpertProvisioner` / `DefaultExpertProvisioner` now implement this. |
| Execution & Synthesis: parallel experts, engram-triggered downstream | ⚠️ | ❌ → ⚠️ | `DataflowBus` wired and backed by `IEngramClient` for publish mirroring. Real Engram-subscription rehydration on restart is a future phase; in-memory subscriptions are functional. |

## §5 — Incident Response & Unanimous Consensus

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| Process Monitor freezes thread, writes Incident Node | ⚠️ | ❌ → ✅ | New `IncidentResponder` hosted service converts non-zero exits + timeouts into `IncidentNode` records, stores to engram `lord_helm_incidents`. |
| Blind Voting panel of distinct AI CLIs | ⚠️ | ❌ → ✅ | New `CliPanelVoter` wraps any `IAgentOutcomeModelClient` from `McpEngramMemory.Core`. Program.cs composes three voters (claude/gemini/codex) and binds them to `DiagnosticPanel`. |
| Discovery Check vs prior failures | ⚠️ | ❌ → ⚠️ | `TokenOverlapNoveltyCheck` remains as the default implementation. An engram-recall variant uses `IEngramClient.SearchAsync` under the same interface. |
| Debate Loop with dissent propagation | ✅ | ❌ → ✅ | `DiagnosticPanel` wired; `IncidentResponder` invokes `ResolveAsync`, writes `Resolution` back to engram, escalates deadlock via `ApprovalGate`. |
| Circuit Breaker / deadlock → human escalation | ✅ | ❌ → ✅ | Implemented in `DiagnosticPanel`; `IncidentResponder` performs the engram log + approval-gate escalation. |

## §6 — Command Center UI/UX

| Spec requirement | Implemented | Wired | Gap / Fix |
|---|:-:|:-:|---|
| Dynamic widget grid (auto-instantiate / -destroy) | ⚠️ | ❌ → ✅ | Gridstack.js 10.3.1 loaded via CDN; Home.razor persists layout to localStorage; `WatcherToWidgetBridge` hosted service auto-materializes widgets from Watcher events. |
| Pre-attentive visual tagging (amber host / dashed blue-green sandbox / pulsing red-yellow incident) | ✅ | ✅ | CSS classes + keyframes match spec. Icons remain text strings (non-blocking). |
| Observation Mode: expand to live `[HOST]`/`[SANDBOX]` tagged stdout/stderr | ⚠️ | ❌ → ⚠️ | Expand modal now re-reads `WidgetState` on `OnChanged` events, so it updates live while an event stream is arriving. SSE/WebSocket upgrade for high-volume tailing is a follow-on. |

---

## Council wiring decisions (2026-04-21, engram panel session `lord-helm-wiring-council-2026-04-21`)

| # | Question | Decision |
|---|---|---|
| 1 | Composition root layout | `LordHelm.Web` hosts every `IHostedService`; `LordHelm.Host` is an administrative Spectre console (health checks, one-shot scout runs). |
| 2 | Watcher → Incident glue | `IncidentResponder` hosted service in `LordHelm.Consensus`. |
| 3 | Approval gate UI | `WidgetKind.Approval` auto-materialized by `ApprovalQueueBridge`. |
| 4 | Expert provisioning | `IExpertProvisioner` + `DefaultExpertProvisioner` in `LordHelm.Orchestrator`. |
| 5 | Scout → transpiler cache | `ITranspilerCacheInvalidator` adapter interface; `JitTranspiler` implements it. |
| 6 | Concrete panel voter | `CliPanelVoter` in `LordHelm.Consensus`, wraps `IAgentOutcomeModelClient`. |
| 7 | Engram writes | Single `IEngramClient` facade in `LordHelm.Core` + `EngramClient` impl in `LordHelm.Orchestrator`. |

These decisions are the canonical wiring for Lord Helm until superseded by a new council.
