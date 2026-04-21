# LordHelm.Providers

**Purpose.** Multi-provider CLI orchestration. Wraps the existing `ClaudeCliModelClient`, `GeminiCliModelClient`, and `CodexCliModelClient` from `McpEngramMemory.Core` behind a uniform interface with per-provider rate-limit governors, priority-weighted failover, and normalized output.

Depends on: `LordHelm.Core`, `McpEngramMemory.Core`, `Microsoft.Extensions.Logging.Abstractions`.

## Flow

```
caller wants "generate text from vendor X"
    │
    ▼
MultiProviderOrchestrator.GenerateWithFailoverAsync
    │
    ▼
preferred ProviderConfig.Governor.WaitAsync   ── blocks when sliding-window full
    │
    ▼
ProviderConfig.Client.GenerateAsync(model, prompt, maxTokens, temperature)
    │
    ├── success ─► ProviderResponse { AssistantMessage, ..., Usage }
    └── failure ─► try next candidate per FailoverPolicy (PriorityWeighted | RoundRobin | Disabled)
```

## Public types

- `ProviderConfig(VendorId, DefaultModel, Governor, Client, Priority)` — registration record. `Client` is any `IAgentOutcomeModelClient`.
- `FailoverPolicy` enum — `RoundRobin`, `PriorityWeighted`, `Disabled`.
- `IProviderOrchestrator.GenerateAsync(vendor, model?, prompt, maxTokens, temperature, ct)` — direct call without failover.
- `IProviderOrchestrator.GenerateWithFailoverAsync(preferred, model?, ...)` — tries preferred, falls back by policy.
- `MultiProviderOrchestrator` — default impl. Round-robin index uses `Interlocked.Increment` for fairness.
- `RateLimitGovernor(maxCallsPerWindow, window)` — sliding-window token bucket. `WaitAsync` blocks until a slot is available. `InFlight` returns current count.

## Output normalization

`GenerateAsync` returns a `ProviderResponse` (defined in `LordHelm.Core`). Token usage is estimated from string lengths when the underlying client doesn't surface real counts (the existing `IAgentOutcomeModelClient` returns only `string?`). `ErrorRecord` captures `unknown_vendor`, `null_response`, or `exception` cases.

## Collaborators

- **`McpEngramMemory.Core.Services.Evaluation`** — the three `*CliModelClient` classes already implement `IAgentOutcomeModelClient`. `LordHelm.Web/Program.cs` registers each as a singleton and composes them into `ProviderConfig`s.
- **`LordHelm.Consensus`** — `CliPanelVoter` wraps the same `IAgentOutcomeModelClient`s directly for the diagnostic panel (voting is latency-sensitive and deserves its own path rather than going through the orchestrator).
- **`LordHelm.Orchestrator`** — a future LLM-backed `IGoalDecomposer` will call `GenerateWithFailoverAsync` to decompose goals into DAGs.

## Invariants

1. **CLI subprocesses are one-shot.** The vendor CLIs don't support warm-process reuse; every call is a fresh process.
2. **The governor is fair.** `WaitAsync` is FIFO within its lock.
3. **Failover preserves the original error** when every candidate fails, the caller gets the error from the *preferred* vendor, not the last attempted one.
