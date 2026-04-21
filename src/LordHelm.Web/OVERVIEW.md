# LordHelm.Web

**Purpose.** The Blazor Server command center. Draggable/resizable widget grid, pre-attentive visual tagging, live log tail, approval queue UI. Also the **canonical composition root** for Lord Helm — `Program.cs` wires every `IHostedService` here (Watcher, Scout, IncidentResponder, ApprovalQueueBridge, WatcherToWidgetBridge, SkillStartupLoader).

Depends on: every other `src/LordHelm.*` project, `McpEngramMemory.Core`, `gridstack.js` (CDN).

## Visual grammar

- **Host** widget — solid amber border, `[host]` icon.
- **Sandbox** widget — dashed blue/green border with a soft-pulse animation, `[box]` icon.
- **Incident** widget — pulsing red/yellow border, `!` icon.
- **Approval** widget — double amber border with soft pulse, `[?]` icon + approve/deny buttons.
- **Completed** — solid green border at reduced opacity.

All colour/shape/motion cues are encoded in CSS variables in `wwwroot/app.css`. Icons are text glyphs (intentional — monospace aesthetic). Layout persists to `localStorage` and survives reconnects.

## Public types

### State
- `WidgetKind` — `Subprocess`, `Approval`, `Incident`.
- `WidgetModel(Id, Kind, Label, Env, Status, UpdatedAt, Tail?, PendingApproval?)` — the record a Razor cell renders.
- `WidgetState` — thread-safe view-model. Exposes `OnChanged` event that `Home.razor` subscribes to via `InvokeAsync(StateHasChanged)`. Methods:
  - `Upsert(model)`, `Remove(id)`.
  - `AppendLog(widgetId, line)` — append to the per-widget ring buffer and republish the widget with the updated `Tail`.
  - `ApplyProcessEvent(ProcessEvent)` — convert a Watcher event into a widget upsert.
  - `RegisterPendingApproval(id, PendingApproval)` — called by `ApprovalQueueBridge` when `ApprovalGate.PendingReader` emits.
  - `ResolveApproval(id, approved, reason, gate)` — called by the Razor approve/deny handlers; completes the gate's TaskCompletionSource and updates the widget status.
  - `SpawnDemo(id, label, env)` — synthesizes a fake subprocess lifecycle (used by the `+ spawn` button for dev/demo).

### Hosted services
- `WatcherToWidgetBridge` — consumes `IProcessMonitor.Events` → `WidgetState.ApplyProcessEvent`. This is how widgets auto-instantiate from real subprocesses.
- `ApprovalQueueBridge` — consumes `ApprovalGate.PendingReader` → `WidgetState.RegisterPendingApproval`. This is how the "UI Approval Gate" of spec §3 materialises in the dashboard.
- `SkillStartupLoader` (declared in `Program.cs`) — one-shot scan of `skills/` at boot, writes to SQLite + mirrors each manifest to engram namespace `lord_helm_skills`.

### UI components
- `Components/App.razor` — HTML head + body. Loads gridstack 10.3.1 CSS + JS from jsDelivr CDN, then `wwwroot/helmgrid.js`, then the Blazor web runtime. `@Assets["..."]` pins fingerprinted asset URLs.
- `Components/Pages/Home.razor` — the dashboard. Subscribes to `WidgetState.OnChanged`, renders each `WidgetModel` as a `.grid-stack-item`, handles expand-to-modal, and wires approve/deny handlers to `WidgetState.ResolveApproval`. `OnAfterRenderAsync(firstRender: true)` calls `helmGrid.init("#helm-root", selfRef)` via JS interop.
- `wwwroot/helmgrid.js` — JS interop module. Exports `helmGrid.init`, `applyLayout`, `resetLayout`, `reapply`. Persists layout as `{ id: { x, y, w, h } }` to `localStorage` on `change`/`resizestop`/`dragstop` events. `resetLayout` calls back into Blazor via `dotnetRef.invokeMethodAsync('ReloadWidgets')`.
- `wwwroot/app.css` — the visual grammar above. Keyframes: `pulse` (incident), `pulse-soft` (sandbox/approval).

## Collaborators

- **`LordHelm.Execution`** — `ApprovalGate` (singleton) is injected into `Home.razor` so approve/deny can resolve pending approvals directly.
- **`LordHelm.Monitor`** — `IProcessMonitor` drives widget lifecycles.
- **`LordHelm.Orchestrator`** — `IEngramClient` (via `EngramClient`) persists skill manifests and other state.
- **`LordHelm.Consensus`** — `IncidentResponder` runs alongside the web app; incidents flow into engram and indirectly into `WidgetState` once we add an incident bridge.

## Invariants

1. **Web is the canonical process.** `LordHelm.Host` exists for administrative tasks (health checks, one-shot scout); production runs `LordHelm.Web` and gets every hosted service via DI.
2. **Widgets auto-materialise.** The `+ spawn` button is a dev affordance. Real widget lifetimes come from Watcher events and approval-gate pendings.
3. **Layout persists client-side, not server-side.** The dashboard is stateless across restarts except for `localStorage` layout.
4. **Approvals default to deny.** Closing a browser tab without clicking approve/deny lets the 60 s gate timeout fire.
