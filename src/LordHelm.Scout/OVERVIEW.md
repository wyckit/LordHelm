# LordHelm.Scout

**Purpose.** The capability syncer. Polls installed CLI binaries (`claude --help` + `--version`, same for gemini/codex) on a schedule, parses GNU-style help into a structured `CliSpec`, diffs against the last stored spec, and emits `MutationEvent`s that invalidate the JIT transpiler cache. Promotes stable specs from STM to LTM after N identical cycles, archiving drifted predecessors.

Depends on: `LordHelm.Core`, `LordHelm.Skills` (for `ITranspilerCacheInvalidator`), `McpEngramMemory.Core`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Hosting.Abstractions`.

## Flow

```
timer tick (every 30 min by default)
    │
    ▼
for each ScoutTarget { VendorId, Executable, Parser }:
    run "<exe> --help"  +  "<exe> --version"
    │
    ▼
ICliHelpParser.Parse(helpOutput, versionOutput)  ──►  CliSpec { Version, Flags[], FlagDigest }
    │
    ▼
ICliSpecStore.RecordAsync(spec)
    │
    ├── digest matches active ─► bump stability; promote STM→LTM at threshold
    └── digest differs         ─► archive old active; insert new STM; diff → MutationEvent[]
                                                                           │
                                                                           ▼
                                                                   onMutation callback
                                                                           │
                                                                           ▼
                                                         ITranspilerCacheInvalidator.Invalidate
```

## Public types

### Data model
- `CliFlag` — `Name`, `ShortName`, `Type`, `Default`, `Description`. One row per `--flag` parsed from help.
- `CliSpec` — `VendorId`, `Version`, `Flags[]`, `CapturedAt`, computed `FlagDigest` (SHA-256 of sorted flag tuples). Digest equality drives drift detection.
- `MutationKind` — `Added`, `Removed`, `ChangedDefault`, `ChangedType`, `Promoted`, `Archived`.
- `MutationEvent` — `VendorId`, `FromVersion`, `ToVersion`, `Kind`, `FlagName`, optional `Detail`, `At`.

### Parsers
- `ICliHelpParser` — contract: `VendorId`, `Parse(string helpOutput, string versionOutput, DateTimeOffset capturedAt) : CliSpec`.
- `GnuStyleHelpParser` — regex-based line parser. Recognizes `  -s, --short-name <value>   description` and `      --flag <type>   description`. Extracts version via `\d+\.\d+\.\d+` pattern.

### Storage
- `ICliSpecStore` — `InitializeAsync`, `RecordAsync(spec, stabilityThreshold, ct)`, `GetActiveAsync`, `RecentMutationsAsync`.
- `SqliteCliSpecStore` — two tables: `cli_specs` (composite PK `(vendor, version, flag_digest)` + `lifecycle` column: `stm`|`ltm`|`archived`) and `cli_mutations` (append-only event log). Promotion rule: identical digest for N calls → flip `stm` to `ltm`.

### Runtime
- `ScoutOptions` — `Interval` (default 30 min), `ProbeTimeout` (default 8 s), `StabilityThreshold` (default 3), `Targets[]`.
- `ScoutTarget` — `VendorId`, `Executable`, `ICliHelpParser`.
- `ScoutService` — `BackgroundService`. On each tick: probe all targets in sequence, record each `CliSpec`, invoke `onMutation` callback per emitted `MutationEvent`. Swallows probe exceptions and logs them.

## Collaborators

- **`LordHelm.Skills`** — `ScoutService.onMutation` (wired in Web Program.cs) calls `ITranspilerCacheInvalidator.Invalidate(vendorId, toVersion)`. Scout has no reference to the transpiler implementation; only the interface.
- **`LordHelm.Web`** — Program.cs registers `ScoutService` as `IHostedService` and supplies a default `ScoutOptions` with three targets.
- **`LordHelm.Host`** — admin console may invoke `ScoutService.RunOnceAsync` for a single on-demand poll without starting the hosted loop.

## Invariants

1. **Digest is the unit of equality.** Timestamps are never used to decide drift.
2. **STM→LTM is earned, not granted.** A new spec must survive `StabilityThreshold` consecutive ticks before promotion; drift before then archives the STM entry too.
3. **`onMutation` callbacks must not throw.** Transpiler invalidation is best-effort; Scout keeps running even if the callback fails.
