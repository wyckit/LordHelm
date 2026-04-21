# LordHelm.Execution

**Purpose.** The Execution Router and its two runners. Every tool call an Expert makes flows through this project. The router reads `SkillManifest.ExecEnv`, gates destructive Host actions through the `ApprovalGate` with a SHA-chained audit log, and dispatches to either the `HostRunner` (native .NET subprocess) or the `DockerSandboxRunner` (ephemeral container, zero-trust).

Depends on: `LordHelm.Core`, `LordHelm.Skills`, `Docker.DotNet`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Logging.Abstractions`.

## Flow

```
ExpertRunner  ──►  IExecutionRouter.RouteAsync(skill, args, caller, cliVersion, shell)
                        │
                        ▼
          needs approval?  ─── yes ──►  IApprovalGate.RequestAsync
                        │                          │
                        │                          ▼
                        │              READ      ─► AutoApproved   (audit)
                        │              other     ─► PendingReader  ─► UI resolves or 60s timeout-DENY
                        │                          │
                        │                          └─► audit-chained SQLite write
                        │                          │   + engram decision node
                        ▼                          ▼
      IJitTranspiler.Transpile(skill, args, vendor, cliVersion, shell)
                        │
                        ▼
         skill.ExecEnv ── Host   ──►  IHostRunner.RunAsync     (native process, inherits permissions)
                       ── Docker ──►  ISandboxRunner.RunAsync  (CapDrop=ALL, ReadonlyRootfs,
                                                                NetworkDisabled, Tmpfs workdir,
                                                                Memory/NanoCpus/PidsLimit caps,
                                                                unconditional RemoveContainerAsync)
                       ── Remote ──►  NotSupportedException (reserved)
                        │
                        ▼
            ToolInvocationResult { ExitCode, Stdout, Stderr, Elapsed, RoutedTo, Approved }
```

## Public types

### Routing
- `IExecutionRouter.RouteAsync(manifest, args, caller, cliVersion, shell, ct)` — the only entry point. Returns `ToolInvocationResult`.
- `ToolInvocationResult` — `ExitCode`, `Stdout`, `Stderr`, `Elapsed`, `RoutedTo`, `Approved`.

### Host
- `IHostRunner` / `HostRunner` — runs the transpiled invocation on the local machine via `ProcessStartInfo` with UTF-8 stdio.

### Sandbox
- `SandboxPolicy` — record bundling every hardening knob: `ImageRefWithDigest`, `MemoryBytes`, `NanoCpus`, `PidsLimit`, `NetworkDisabled`, `ReadonlyRootfs`, `TmpfsMounts`, `ReadOnlyBinds`, `WallClockTimeout`. `Default(imageDigest)` returns safe defaults.
- `ISandboxRunner` / `DockerSandboxRunner` — uses `Docker.DotNet`. Image ref is validated to contain `@sha256:` (digest pin). Creates, starts, waits, and always removes the container in a `finally` block. Demuxes the 8-byte Docker frame header from multiplexed log streams.

### Approval
- `RiskTier`/`TrustLevel` — declared in `LordHelm.Core`. Consumed here.
- `HostActionRequest` — `SkillId`, `RiskTier`, `Summary`, optional `DiffPreview`, `OperatorId`, `SessionId`.
- `ApprovalDecision` — `Approved`, `Reason`, `DecidedAt`, `UsedBatchToken`.
- `IApprovalGate.RequestAsync(req, ct)` — READ auto-approves; other tiers queue onto `PendingReader` with 60 s timeout-default-deny. Session-scoped batch tokens via `GrantBatchToken` pre-approve same-tier bulk operations.
- `ApprovalGate.PendingReader` — `ChannelReader<PendingApproval>` consumed by the Blazor `ApprovalQueueBridge`.
- `ApprovalGate.Resolve(pending, approved, reason)` — UI calls this when operator clicks approve/deny.
- `IAuditLog` / `SqliteAuditLog` — append-only SHA-256-chained audit trail. `VerifyChainAsync` returns false if any row has been tampered with. Every approval decision writes both here and to engram (category `decision`).
- `AuditEntry` — `Id`, `PrevHashHex`, `EntryHashHex`, `SkillId`, `RiskTier`, `Decision`, `OperatorId`, `SessionId`, optional `Detail`, `At`.

## Collaborators

- **`LordHelm.Skills.Transpilation`** — router calls `IJitTranspiler.Transpile` after the approval gate clears.
- **`LordHelm.Orchestrator`** — `DefaultExpertProvisioner` produces an `ExpertRunner` closure that calls the router.
- **`LordHelm.Web`** — `ApprovalQueueBridge` hosted service consumes `PendingReader` and surfaces a `WidgetKind.Approval` widget so the operator can approve or deny in the dashboard.

## Invariants

1. **Docker images must be digest-pinned.** `DockerSandboxRunner` throws on plain tags; supply-chain drift is not permitted.
2. **Container removal is unconditional.** The removal call is in a `finally` block and is best-effort even if cancelled.
3. **Audit entries are immutable and chained.** `VerifyChainAsync` must return true in production; a false return means the SQLite file has been tampered with.
4. **Timeout default is DENY.** If no operator responds within 60 seconds, the `ApprovalDecision.Approved` is `false`.
