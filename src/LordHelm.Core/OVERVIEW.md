# LordHelm.Core

**Purpose.** The foundation layer. Contains only domain records and cross-cutting interfaces — no behaviour, no I/O, no framework dependencies. Every other project in the solution references this one. Depends on nothing from our codebase.

## Public types

| Type | Kind | Meaning |
|---|---|---|
| `ExecutionEnvironment` | enum | Where a skill runs: `Host`, `Docker`, `Remote`. Drives the Execution Router's dispatch. |
| `RiskTier` | enum | Five-tier classification of destructive host actions: `Read`, `Write`, `Delete`, `Network`, `Exec`. Consumed by the Approval Gate. |
| `TrustLevel` | enum | Required trust level for a skill invocation: `None` < `Low` < `Medium` < `High` < `Full`. |
| `TargetShell` | enum | Shell-quoting dialect selector: `Bash`, `PowerShell`, `Cmd`. Passed through the transpiler. |
| `SemVer` | record | Parsed semver (`Major.Minor.Patch[-Prerelease]`). Used on `SkillManifest`. |
| `SkillManifest` | record | The canonical AST after parsing a `.skill.xml`: `Id`, `Version`, `ContentHashSha256`, `ExecEnv`, `RequiresApproval`, `RiskTier`, `Timeout`, `MinTrust`, `ParameterSchemaJson`, `CanonicalXml`. The SHA-256 is the immutable identity — changing the canonicalization algorithm invalidates every stored hash. |
| `ExpertProfile` | record | Ephemeral agent persona: `ExpertId`, `CliVendorId`, `Model`, `SkillLoadout`, `GoalContext`. Produced by `IExpertProvisioner` at runtime. |
| `ProviderResponse` | record | Normalized output from any provider: `AssistantMessage`, `ToolCalls`, `Usage`, optional `Error`. Lets the orchestrator treat claude/gemini/codex responses uniformly. |
| `ToolCall`, `UsageRecord`, `ErrorRecord` | records | Sub-parts of `ProviderResponse`. |
| `SandboxResult` | record | `ExitCode`, `Stdout`, `Stderr`, `Elapsed`. Returned by every sandbox run. |
| `IEngramClient` | interface | Single facade over McpEngramMemory.Core: `StoreAsync`, `SearchAsync`, `GetAsync`, `IsAvailableAsync`. Every layer that needs engram talks only through this abstraction. |
| `EngramHit` | record | Search/get result shape: `Namespace`, `Id`, `Text`, `Score`, `Metadata`. |

## Collaborators

- **Consumed by every project** — all records and enums flow outward from here. Keeping it dependency-free is a load-bearing architectural rule.
- **Implementations live elsewhere** — `EngramClient` is in `LordHelm.Orchestrator` (it needs `McpEngramMemory.Core`).

## Invariants

1. **No external dependencies.** This project must not add package references other than what the .NET BCL provides. Anything that needs `McpEngramMemory.Core`, `Docker.DotNet`, etc. belongs in a higher layer.
2. **Records are immutable.** Every domain type here is `record` with positional parameters. Treat them as value objects.
3. **`SkillManifest.ContentHashSha256` is the identity.** Any change to canonicalization inside `LordHelm.Skills` must preserve the property that two whitespace-equivalent manifests produce the same hash.
