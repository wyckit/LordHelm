# LordHelm.Consensus

**Purpose.** The Unanimous Consensus Protocol. Turns sandbox failures into `IncidentNode` records, convenes a blind-voting panel of distinct AI CLIs, propagates minority dissent across up to N rounds, runs a novelty check against prior failures, and escalates deadlocks to the human operator via the Approval Gate.

Depends on: `LordHelm.Core`, `LordHelm.Providers`, `LordHelm.Monitor`, `McpEngramMemory.Core`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`.

## Flow

```
Watcher emits ProcessEventKind.Exited (non-zero) or ProcessEventKind.Incident
    │
    ▼
IncidentResponder.HandleIncidentAsync
    │
    ├── build IncidentNode (skill, args hash, exit, stdout tail, stderr tail)
    │
    ▼
IEngramClient.StoreAsync(ns="lord_helm_incidents", category="incident")
    │
    ▼
IConsensusProtocol.ResolveAsync(incident)
    │
    ▼
DiagnosticPanel.ResolveAsync
    │
    ▼  round 1..MaxRounds
    │
    │   parallel: every IPanelVoter.VoteAsync(incident, lastDissent)   ── blind: they don't see each other
    │       │
    │       ▼
    │   unanimous YES?
    │       │
    │       ├── yes ─► novelty check (prior failure log)
    │       │         │
    │       │         ├── novel  ─► return Resolution { Unanimous, AgreedFix }
    │       │         └── stale  ─► escalate (matches a prior failure)
    │       │
    │       └── split ─► propagate minority rationale into next round
    │
    ▼  after MaxRounds without unanimity:
    return Resolution { EscalatedToHuman, EscalationReason }
    │
    ▼
IEngramClient.StoreAsync(ns="lord_helm_incidents", category="resolution"|"escalation")
```

## Public types

### Domain
- `IncidentNode(IncidentId, SkillId, ArgsHash, ExitCode, Stdout, Stderr, At)`.
- `PanelVote(VoterId, Approve, Rationale, ProposedFix, Confidence)`.
- `PanelRound(RoundNumber, Votes[], Unanimous, Deadlock?)`.
- `Resolution(Unanimous, AgreedFix?, Rounds[], EscalatedToHuman, EscalationReason?)`.

### Contracts
- `IPanelVoter` — `VoterId`, `VoteAsync(incident, dissent, ct) : PanelVote`. Independent decision per voter; never sees other voters.
- `INoveltyCheck.IsNovelAsync(proposedFix, ct)` — returns false when the proposed fix has matched a prior escalated failure (prevents retry loops).
- `IConsensusProtocol.ResolveAsync(incident, ct)` — the protocol entry point.

### Implementations
- `CliPanelVoter(voterId, IAgentOutcomeModelClient, model, logger?)` — wraps claude/gemini/codex model clients. Builds a JSON-constrained prompt, calls `GenerateAsync`, parses `{ approve, rationale, fix, confidence }`. Robust to non-JSON or malformed responses (defaults to a conservative deny).
- `TokenOverlapNoveltyCheck` — in-memory Jaccard token-overlap stand-in for embedding-based recall. `RememberPriorFailure(fix)` seeds the failure log. Swap with an engram-backed variant in production.
- `DiagnosticPanel(panel, novelty, options, logger)` — main orchestration. Enforces `MinActiveVoters` floor, `MaxRounds` ceiling, and dissent-propagation prompt injection.
- `DiagnosticPanelOptions` — `MaxRounds = 3`, `MinActiveVoters = 2`.

### Hosted service
- `IncidentResponder : BackgroundService` — consumes `IProcessMonitor.Events`. Buffers stdout/stderr per subprocess from `Started` until `Exited`/`Incident`. On failure, constructs an `IncidentNode`, persists it, invokes `IConsensusProtocol.ResolveAsync`, and persists the `Resolution`. Never throws; exceptions are logged and the loop continues.

## Collaborators

- **`LordHelm.Monitor`** — source of `ProcessEvent`s.
- **`LordHelm.Core`** — `IEngramClient` facade for incident + resolution persistence.
- **`LordHelm.Execution`** — on escalation, `IApprovalGate` receives a synthetic `HostActionRequest` so a human can approve a manual recovery action.
- **`McpEngramMemory.Core`** — `CliPanelVoter` wraps the existing CLI model clients as voters without duplicating CLI subprocess code.

## Invariants

1. **Blind voting.** Voters never see each other's votes within a single round. Only the *next* round receives an injected minority summary.
2. **Hard round ceiling.** `MaxRounds = 3` by default. On expiration the protocol escalates — it does not loop indefinitely.
3. **Novelty check short-circuits stale fixes.** If the panel agrees on a fix that was already tried and failed, the protocol escalates rather than repeating.
4. **The responder never drops events silently.** Non-zero exits without a preceding `Started` are still converted to incidents (best-effort, with empty stdout/stderr).
