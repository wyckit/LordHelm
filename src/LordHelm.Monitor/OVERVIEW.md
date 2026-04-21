# LordHelm.Monitor

**Purpose.** The Autonomous Process Monitor (the Watcher). Continuous background daemon that launches subprocesses, streams stdout/stderr line-by-line, samples CPU/RSS every 2 seconds, and emits typed `ProcessEvent`s onto an unbounded channel. Downstream services (`WatcherToWidgetBridge`, `IncidentResponder`) subscribe to the channel.

Depends on: `LordHelm.Core`, `CliWrap`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`.

## Flow

```
Watcher.Launch(LaunchSpec)  ──►  CliWrap.ListenAsync (event stream)
                                    │
                                    ├── StartedCommandEvent            ─► Started event
                                    ├── StandardOutputCommandEvent     ─► Stdout event  (+ LogRing append)
                                    ├── StandardErrorCommandEvent      ─► Stderr event  (+ LogRing append)
                                    └── ExitedCommandEvent             ─► Exited event
                                    │
                                    ▼
                         Channel<ProcessEvent> Events
                                    │
                ┌───────────────────┴────────────────────┐
                ▼                                        ▼
     WatcherToWidgetBridge                        IncidentResponder
     (updates WidgetState)                        (non-zero exit → IncidentNode → engram + Consensus)
```

## Public types

- `ProcessEventKind` enum — `Started`, `Stdout`, `Stderr`, `Exited`, `ResourceSample`, `Incident`.
- `ProcessEvent` — `SubprocessId`, `Label`, `Kind`, optional `Line`, optional `ExitCode`, optional `CpuFraction`, optional `WorkingSetBytes`, `At`.
- `LaunchSpec` — `Executable`, `Arguments`, `Label`, `SubprocessId`, optional `Timeout`, optional `Env`.
- `ProcessHandle` — `SubprocessId`, `Task<int> ExitTask`, `LogRing Logs`.
- `LogRing(capacity = 512)` — thread-safe circular buffer. `Append(string)`, `Snapshot()`. Used for per-subprocess tailing.
- `IProcessMonitor` — `Events` (`ChannelReader<ProcessEvent>`), `Launch(LaunchSpec, ct)`, `Logs` (read-only map of `SubprocessId → LogRing`).
- `Watcher` — default implementation. Registered both as `Watcher` and aliased as `IProcessMonitor`.

## Collaborators

- **`LordHelm.Web`** — registers `Watcher` as singleton and `IProcessMonitor`. `WatcherToWidgetBridge` hosted service consumes `Events` and drives `WidgetState.ApplyProcessEvent` so the Blazor grid auto-materialises widgets per subprocess.
- **`LordHelm.Consensus`** — `IncidentResponder` consumes the same `Events` channel; non-zero exits and explicit `Incident` events are converted into `IncidentNode`s and handed to `IConsensusProtocol`.
- **`LordHelm.Execution`** — future: routers can optionally launch through the Watcher so Host and Sandbox subprocess lifetimes are uniformly tracked in the observability grid.

## Invariants

1. **One `LogRing` per subprocess.** The ring is created in `GetOrAdd`, so subscribers can read logs even if `Launch` hasn't finished setting up the channel.
2. **The events channel is never completed implicitly.** Only `DisposeAsync` calls `TryComplete`. Tests that spin up a Watcher should dispose it to let background consumers exit cleanly.
3. **Timeouts kill via cancellation, not signals.** `LaunchSpec.Timeout` is a linked-CTS deadline; on expiration the process receives a cancellation token and is killed by CliWrap.
