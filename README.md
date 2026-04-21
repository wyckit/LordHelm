# Lord Helm

> *One ring to rule them all.*

**Lord Helm** is a .NET 9 Agentic Command Center that wraps local AI CLIs (`claude`, `gemini`, `codex`) behind a uniform orchestration layer, coordinates ephemeral Expert agents through a shared engram-memory graph, segregates execution between a trusted Host and hardened Docker sandboxes, and self-heals failed runs via a **Unanimous Consensus Protocol**.

It exists because the CLI is the only sanctioned path for subscription-authenticated access to providers like Claude (the OAuth token is rejected over HTTP). Lord Helm turns that workaround into a production orchestration surface.

---

## Highlights

- **Composition over inheritance.** Skills are reusable capabilities stored as hybrid XML+JSON manifests; Experts are ephemeral personas that receive a *Loadout* of skills at runtime.
- **Persistent shared brain.** Agents read and write to nodes in `McpEngramMemory` rather than communicating point-to-point. Downstream tasks trigger when prerequisite nodes populate (blackboard/dataflow pattern).
- **Two execution environments, always gated.** Host actions run natively with an Approval Gate for destructive tiers; Docker runs ephemeral containers with `CapDrop=ALL`, `ReadonlyRootfs`, `Tmpfs` workdir, PID/CPU/memory caps, digest-pinned images, and unconditional cleanup.
- **Pre-attentive observability.** Blazor Server widget grid: solid amber + shield for Host, dashed blue/green + container for Sandbox, pulsing red/yellow for Incidents. Real-time `[HOST]` / `[SANDBOX]` tagged log tails.
- **Self-healing consensus.** On sandbox failure, a Diagnostic Panel of distinct CLIs votes blindly, propagates dissent for up to 3 rounds, verifies proposed fixes against the failure log (novelty check), and escalates to a human on deadlock.
- **Scout Protocol.** A background service polls `claude --help` / `gemini --help` / `codex --help` on a schedule, diffs against stored `CliSpec` nodes, and invalidates transpiler caches when flags drift.

---

## Repository Layout

```
LordHelm/
├── LordHelm.slnx                 # .NET 10 SDK's XML solution format
├── Directory.Build.props         # net9.0, TreatWarningsAsErrors, nullable, implicit usings
├── src/
│   ├── LordHelm.Core/            # Domain: SkillManifest, ExpertProfile, enums, records
│   ├── LordHelm.Skills/          # XSD schema, canonicalizer, validator, SQLite cache,
│   │                             # FileSystemWatcher, JIT transpiler, flag tables, shell escaper
│   ├── LordHelm.Scout/           # Help-output parsers, CliSpec store, MutationEvent log, ScoutService
│   ├── LordHelm.Execution/       # ExecutionRouter, DockerSandboxRunner, HostRunner,
│   │                             # ApprovalGate, SHA-chained AuditLog
│   ├── LordHelm.Providers/       # MultiProviderOrchestrator, RateLimitGovernor, ProviderResponse
│   ├── LordHelm.Monitor/         # CliWrap-based Watcher, LogRing, ProcessEvent stream
│   ├── LordHelm.Orchestrator/    # LordHelmManager, GoalDecomposer, DataflowBus, TaskDag
│   ├── LordHelm.Consensus/       # DiagnosticPanel, IPanelVoter, IncidentNode, NoveltyCheck
│   ├── LordHelm.Mcp/             # ILordHelmMcpServer contracts (dispatch_goal, list_skills, ...)
│   ├── LordHelm.Web/             # Blazor Server command center
│   └── LordHelm.Host/            # Spectre.Console host + DI composition root
├── tests/
│   ├── LordHelm.Core.Tests/
│   ├── LordHelm.Skills.Tests/
│   ├── LordHelm.Execution.Tests/
│   └── LordHelm.Consensus.Tests/
└── skills/                       # On-disk canonical skill manifest library
    ├── read-file.skill.xml
    ├── execute-python.skill.xml
    └── write-engram-node.skill.xml
```

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| Windows 11 | Primary target (WSL2-backed Docker Desktop) |
| .NET SDK | **9.0** or **10.0** (project multi-targets but defaults to net9.0) |
| Docker Desktop | Linux-containers mode with WSL2 backend |
| Claude CLI | On PATH (`claude --version` must succeed) |
| Gemini CLI | Optional — required for cross-provider failover |
| Codex CLI | Optional — required for 3-voter consensus panels |
| `McpEngramMemory.Core` | Referenced via project reference to `../mcps/mcp-engram-memory` |

---

## Build

```bash
dotnet build LordHelm.slnx
```

Clean build produces **0 errors, 0 warnings** from Lord Helm's own code. A narrow `NoWarn="NU1903"` is declared on `System.Security.Cryptography.Xml` in `LordHelm.Skills.csproj` — the advisories concern XML-signature verification on untrusted input, which Lord Helm does not do (it uses C14N only to canonicalize locally-authored manifests for hashing).

## Test

```bash
dotnet test LordHelm.slnx
```

Current suite: **41 tests, all green** across `LordHelm.Skills.Tests` (24), `LordHelm.Execution.Tests` (14), and `LordHelm.Consensus.Tests` (3).

## Run

```bash
# Startup health check (Spectre banner + Docker/CLI probes)
dotnet run --project src/LordHelm.Host

# Blazor command center UI (default ASP.NET port)
dotnet run --project src/LordHelm.Web
```

### Windows PowerShell scripts

For first-time setup and day-to-day launches:

```powershell
# Verify prerequisites, restore, build, test, pre-pull sandbox image
.\scripts\install.ps1

# Launch host health check + Blazor dashboard (default)
.\scripts\start.ps1

# Just the dashboard on a custom URL
.\scripts\start.ps1 -WebOnly -Url http://localhost:7080

# Just the headless host composition root
.\scripts\start.ps1 -HostOnly

# One-shot health check
.\scripts\start.ps1 -HealthOnly
```

`install.ps1` accepts `-SkipTests`, `-SkipDockerPull`, and `-SandboxImage <digest-pinned-ref>` to customize the bootstrap.

---

## Key Design Invariants

These are load-bearing. Changing any of them invalidates stored artifacts.

### 1. Skill manifest canonicalization

Pipeline: parse XML with `PreserveWhitespace=false` → compact the embedded JSON Schema CDATA → W3C **Exclusive Canonical XML 1.0** (`XmlDsigExcC14NTransform`) → **SHA-256** of the UTF-8 bytes → lowercase hex. The hash is the skill's immutable identity. Two manifests with identical content but different whitespace (XML *or* JSON-in-CDATA) must produce the same hash.

### 2. Two-stage validation, XSD-first

`XmlSchemaSet` validates the envelope; only if XSD passes does `NJsonSchema` parse the CDATA and require `$schema` to declare Draft 2020-12. Validation never throws from the loader — errors are returned in a `ValidationReport` so one bad file can't poison the scan.

### 3. SQLite primary, engram secondary

Every persistent store writes to SQLite in the critical path and fires an engram write asynchronously. Engram failures are non-fatal. This applies to the skill cache, audit log, and CliSpec store.

### 4. Sandbox hardening defaults

`DockerSandboxRunner` refuses to launch an image unless pinned by digest (`image@sha256:...`). It enforces:
- `CapDrop = ["ALL"]`
- `ReadonlyRootfs = true`
- `NetworkMode = "none"` (unless the policy opts in)
- `Tmpfs = { "/work" : "rw,noexec,nosuid,size=64m" }`
- `Memory`, `NanoCPUs`, `PidsLimit` caps
- `RemoveContainerAsync(force=true)` in an unconditional `finally`

### 5. Approval-gate risk tiers

```
Read   -> auto-approve (logged to audit)
Write  -> prompt, 60-second timeout-default-DENY
Delete -> prompt with diff preview
Network-> prompt with endpoint disclosure
Exec   -> highest-trust prompt
```

Session-scoped "trust this batch" tokens allow pre-approving same-tier bulk work. The audit log is a **SHA-256-chained** append-only table; `IAuditLog.VerifyChainAsync` detects tampering.

### 6. Consensus protocol

- Blind simultaneous voting by all panelists (no cross-visibility).
- Unanimous YES → novelty check vs. failure log → apply fix.
- Split vote → minority rationale injected into majority context → re-vote.
- Hard cap at **3 rounds** → escalate through the same `ApprovalGate` as destructive host actions.
- Novelty check prevents structurally-identical retries from looping.

---

## Skill Manifest Format

A skill is an XML file under `skills/` with the extension `.skill.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Skill xmlns="https://lordhelm.dev/schemas/skill-manifest/v1">
  <Id>read-file</Id>
  <Version>0.1.0</Version>
  <ExecutionEnvironment>Host</ExecutionEnvironment>
  <RequiresApproval>false</RequiresApproval>
  <RiskTier>Read</RiskTier>
  <Timeout>PT30S</Timeout>
  <MinTrust>Low</MinTrust>
  <Description>Read a UTF-8 text file from the host filesystem.</Description>
  <Tags>
    <Tag>filesystem</Tag>
    <Tag>read</Tag>
  </Tags>
  <ParameterSchema><![CDATA[{
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "required": ["path"],
    "properties": {
      "path":     { "type": "string", "minLength": 1 },
      "maxBytes": { "type": "integer", "minimum": 1, "maximum": 1048576 }
    },
    "additionalProperties": false
  }]]></ParameterSchema>
</Skill>
```

**Constraints** enforced by `skill-manifest.xsd`:
- `Id` must match `[a-z][a-z0-9-]*[a-z0-9]`, length 2–128.
- `Version` must be semver (`major.minor.patch[-prerelease]`).
- `Timeout` is an ISO-8601 duration (`PT30S`, `PT2M`).
- Elements must appear in the order declared (strict `xs:sequence`).
- `<ParameterSchema>` is an opaque `xs:string`; JSON Schema validation is a separate pass.

---

## Engram Integration

Lord Helm references `McpEngramMemory.Core` as a project reference to the sibling `../mcps/mcp-engram-memory/` repository. Key namespaces used:

| Namespace | Purpose |
|---|---|
| `lord_helm_skills` | Skill manifests (mirrors on-disk form) |
| `lord_helm_incidents` | Incident nodes from sandbox failures |
| `lord_helm_audit` | Approval-gate decisions (also in SQLite) |
| `expert_docker_sandbox_orchestrator`, `expert_skill_manifest_architect`, ... | 9 Lord-Helm-specific experts (see below) |

### Experts seeded for Lord Helm

The following experts were created and seeded with foundation memories before the project was coded; use them via `dispatch_task` for design questions in each area:

- `docker_sandbox_orchestrator`
- `skill_manifest_architect`
- `cli_transpiler_engineer`
- `cli_capability_scout`
- `agent_observability_ui_engineer`
- `approval_gate_ux_designer`
- `debate_protocol_designer`
- `engram_dataflow_orchestrator`
- `multi_provider_cli_orchestrator`

---

## Reused Assets (DO NOT reimplement)

Lord Helm reuses the battle-tested CLI drivers from `McpEngramMemory.Core`:

- `ClaudeCliModelClient` — stdin-piped, UTF-8-forced, 5-minute timeout
- `CodexCliModelClient` — same shape
- `GeminiCliModelClient` — same shape
- `CliExecutableResolver` — Windows PATHEXT and `.cmd`-shim resolution

They live at `C:/Software/mcps/mcp-engram-memory/src/McpEngramMemory.Core/Services/Evaluation/`.

---

## Phase Plan (for future work)

| Phase | Status | Focus |
|-------|--------|-------|
| 0 — Scaffold | done | 15 projects, DI composition root, health check |
| 1 — Skill manifest | done | XSD + C14N + SQLite cache + FileSystemWatcher |
| 2 — Scout + Transpile | done | `--help` parsing, STM→LTM, JIT transpiler with LRU cache |
| 3 — Execution | done | Router, Docker sandbox, host runner, Approval Gate, audit chain |
| 4 — Monitor + Providers + Bus | done | Watcher, rate limiting, provider failover, dataflow bus, DAG |
| 5 — Manager + Consensus | done | Goal decomposition, Diagnostic Panel, novelty check |
| 6 — Web + MCP | done | Blazor command center, MCP server contract |

Follow-on work:
- Replace `PassthroughGoalDecomposer` with an LLM-backed decomposer using the `MultiProviderOrchestrator`.
- Wire `ScoutService` to invalidate `JitTranspiler` via its `Action<MutationEvent>` callback in the Host composition root.
- Ship an MCP JSON-RPC transport for `ILordHelmMcpServer` so upstream agents can call `dispatch_goal`.
- Live-wire the Blazor `WidgetState` to `Watcher.Events` via a hosted service that forwards `ProcessEvent`s.
- End-to-end acceptance test per the approved plan (`~/.claude/plans/steady-jingling-dragonfly.md`).

---

## License

Internal / not yet licensed. Engram-backed assets inherit the license of the `mcp-engram-memory` repository.
