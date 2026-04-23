# Handoff: Lord Helm — Command Center Dashboard

## Overview
Lord Helm is a command center dashboard for monitoring and orchestrating a fleet
of mixed-domain AI agents (code, research, data, ops, writing, design). The UI
is customizable: widgets can be added, removed, and reordered; the central
"Lord Helm" orchestrator can generate tailored layouts in response to natural-
language prompts; and any agent can be drilled into for deep inspection.

## About the Design Files
**The files in this bundle are design references created in HTML/React+Babel.**
They are prototypes showing intended look and behavior, not production code to
ship directly. Your task is to **recreate these designs in the target codebase's
existing environment** (React/Next.js, Vue, SwiftUI, etc.) using its established
patterns, state management, and component library. If no environment exists
yet, choose the most appropriate framework for a real-time monitoring dashboard
(React + a lightweight state store like Zustand, plus a WebSocket/SSE stream
for live agent telemetry, is a strong default).

## Fidelity
**High-fidelity.** Colors, typography, spacing, interactions, and animations are
all final. Recreate pixel-perfectly using the target codebase's existing primitives.

## Design Tokens

### Colors
```
/* Backgrounds (warm dark neutral scale) */
--bg-0: #0a0a0b   /* app bg */
--bg-1: #0f1012   /* header / panel bg */
--bg-2: #15171a   /* widget bg (top of gradient) */
--bg-3: #1c1f24   /* raised surface / input bg */
--bg-4: #24282f
--bg-5: #2d323a

/* Lines */
--line-1: rgba(255,255,255,0.06)  /* default widget border */
--line-2: rgba(255,255,255,0.09)
--line-3: rgba(255,255,255,0.14)

/* Text */
--fg-0: #f4f5f7   /* primary */
--fg-1: #d7dae0
--fg-2: #9ba0a9   /* secondary */
--fg-3: #6b6f78   /* tertiary / meta */
--fg-4: #4a4e57   /* disabled */

/* Brand (Lord Helm: muted royal indigo → cyan accent) */
--brand:    #8b7cff
--brand-2:  #6d5efc
--accent:   #5ee4d5   /* teal — used for "now" markers, telemetry packets */
--brand-glow: rgba(139,124,255,0.35)

/* Semantic */
--ok:   #4ade80   (bg: rgba(74,222,128,0.12))
--warn: #fbbf24   (bg: rgba(251,191,36,0.12))
--err:  #f87171   (bg: rgba(248,113,113,0.12))
--info: #60a5fa   (bg: rgba(96,165,250,0.12))
--idle: #6b6f78

/* Agent-type hues (used throughout: glyphs, pills, topology nodes) */
--hue-code:     #8b7cff   /* ‹/› */
--hue-research: #5ee4d5   /* ◎ */
--hue-data:     #fbbf24   /* ▦ */
--hue-ops:      #f87171   /* ⚙ */
--hue-write:    #f0abfc   /* ✎ */
--hue-design:   #60a5fa   /* ◈ */
```

### Typography
- **Sans**: `Inter` (400/500/600/700) — body, UI labels. Feature settings: `"cv11", "ss01", "ss03"`.
- **Mono**: `JetBrains Mono` (400/500) — log streams, metrics, IDs, paths, timestamps. Always `font-variant-numeric: tabular-nums`.
- **Display serif**: `Instrument Serif` italic — the "Lord Helm" wordmark only.

### Spacing / Radii
- Widget gap: `10px`
- Widget padding: head `12px 14px`, body varies
- Radii: `6` (sm), `10` (md), `14` (lg, widgets + chat), `20` (xl)

### Shadows
- `--shadow-1`: subtle 1px border + ambient drop
- `--shadow-2`: `0 8px 24px -6px rgba(0,0,0,0.5)` + border — menus/modals
- `--shadow-glow`: brand ring + 32px purple glow — focused/expanded state

## Layout

Top-level structure (top → bottom):
1. **Header bar** (`48px`): crest + wordmark, fleet/env selector, live agent counts (working / waiting / blocked / idle), "Edit layout" toggle, "+ Add widget" button (visible in edit mode).
2. **Helm-generated banner** (optional, appears when Helm rearranges the layout). 1-line announcement + "Restore default" button.
3. **Throne** (`~80px`): large gradient crest + inline command input with rotating placeholder example + quick-action chips. Pressing Enter with a prompt-matching preset triggers a layout rearrangement; any non-matching prompt opens the floating chat.
4. **Widget grid**: 12-column CSS grid, `gridAutoRows: minmax(110px, auto)`, `gap: 10px`. Fills remaining viewport and scrolls as needed. Each layout entry has `{ id, gx, gy, w, h }` — only `w` and `h` are actually used (via `gridColumn: span N` / `gridRow: span N`); positions are implicit.
5. **Floating chat panel** (optional, bottom-right): 380×520, appears when user opens Lord Helm chat.

## Widgets (11 total)

All widgets share a common frame:
- Linear gradient bg `var(--bg-2)` → `var(--bg-1)`
- 1px `--line-1` border, 14px radius
- Head row: uppercase 12px semibold title with glyph icon + right-aligned action buttons (`⊞` expand, `⋯` options)
- Edit mode: dashed border, floating `⋮⋮` drag handle top-left, floating `−` remove handle top-right
- Expand: promotes to `grid-column: 1/-1` and `grid-row: span 3`, adds brand glow

### 1. Fleet Roster (`fleet`, default 3×3)
Agents grouped by status (Working / Waiting / Blocked / Idle), each group with a
header showing pip + count. Each row: 22px type-glyph chip, name + current task,
progress % on the right when working. Click selects (drives Active Agent &
Topology highlight); double-click opens deep-dive.

### 2. Active Agent (`active`, 4×2)
Detail header: type chip, name + status pip, model/tools line, "Deep dive" button.
Task text, progress bar, 4-metric strip (Runtime / Tokens / Cost / Progress in mono).
Below: scrolling log stream with 3-col grid `56px 48px 1fr` (timestamp / level tag /
content). Levels: `tool` (brand color), `out` (ok), `think` (italic fg-3), default fg-1.
Blinking cursor at the tail while status === working.

### 3. Topology (`topology`, 5×2) — **the centerpiece**
- Lord Helm at center as a 56–80px rounded square with gradient bg + rotating aura rings.
- 4 "pod" orchestrator nodes (Write / Code / Data / Ops) at intermediate radii, 34–54px circles with accent border.
- 12 agent nodes around the edges, 22–40px circles in agent-type color.
- **Dynamic sizing**: every node grows based on its load (agents: blend of `progress * 0.6 + tokens/200k * 0.4`; pods: aggregate child load; Helm: sum across fleet). `transition: width/height 400ms cubic-bezier(.2,.7,.3,1)`.
- **Load ring**: thin arc around each agent/pod showing its load %.
- **Edges**: styled by destination load — faint purple when idle, dashed teal when active, red when destination is blocked. Dashes animate with `stroke-dashoffset` to imply flow.
- **Animated packets**: 90ms tick spawns new packets on a 4-tick cadence. Working agents emit teal telemetry packets (agent → pod → helm, 20 ticks per hop); blocked agents emit red alert packets; Helm emits purple command packets to random pods. Packets are 3 stacked SVG circles (r=0.5/0.9/1.6 @ 1/0.35/0.15 opacity) moving linearly along their edge.
- Grid backdrop (28px), radial brand glow at center.
- Hover tooltip: name, load %, token count, current task, "dbl-click for deep dive" hint.
- Double-click any agent node → opens deep-dive overlay.

### 4. KPIs (`kpis`, 3×1)
2×2 grid separated by 1px `--line-1` dividers. Each tile: label, large value (22px
semibold tabular-nums), delta % colored by direction (green up / red down, **inverted
for Spend**), sparkline polyline + 15% opacity area fill. Default KPIs: Tasks/hr,
Success rate, Tokens/min, Spend/day.

### 5. Timeline / Gantt (`gantt`, 6×2)
6-hour window, ticks every hour labeled `−Nh`. Each row: 92px name label + flex
track. Done segments render at 40% of the agent's color over `--bg-3`; working
segments render at full color with a sweeping white gradient (`sweep` keyframe,
2s linear). Blocked segments in `--err`. A vertical teal `--accent` "now" line
spans the track with a 6px glow.

### 6. Task Queue (`queue`, 3×2)
Rows: priority tag (`p1/p2/p3` in mono, red/amber/gray), title + "→ assignee ·
est Xm", "Assign" button. Rows separated by 1px line.

### 7. Alerts (`alerts`, 3×2)
Rows: colored tag-box (err/warn/info/ok with symbol inside), title + mono meta,
relative time on the right.

### 8. System Health (`health`, 3×1)
2×2 grid of metered bars: label + value in mono, 4px progress bar colored by
threshold (≤60% ok, ≤80% warn, >80% err), optional note below in mono.

### 9. Approvals (`approvals`, 3×2)
Rows: agent name + risk pill (high/med/low with matching tint), action text,
Approve (brand-filled) + Deny (outlined) buttons.

### 10. Recent (`recent`, 3×2)
Completed artifacts list: kind icon (pr/artifact/doc/report), mono filename + agent,
relative time.

### 11. Chat with Lord Helm (`chat`, 3×3)
Message list (helm bubbles left w/ crest, user bubbles right brand-filled), typing
indicator with pulsing dots, 3 suggestion chips, input bar with crest glyph and
`⏎` hint.

## Throne (standalone component above grid)
- Gradient indigo→teal card, 14px radius, 1.5px border (brand when focused).
- 40px Lord Helm crest chip, "Lord Helm" display-serif italic name, working pip + "Orchestrating · N agents" uppercase meta.
- Input with rotating placeholder — examples cycle every 2800ms. On Enter, check
  prompt against `HELM_PRESETS` regex map; if match, apply that layout + show
  banner; else open chat.
- Quick-action chips row: "Generate dashboard for incident view", "Focus on code
  agents", "What changed overnight?", "Optimize fleet".
- `⌘K` kbd hint.

## Helm-Generated Layouts (Prompt → Preset)
5 presets matched by regex:
- `/incident|alert|block|fire|break|fail|error|issue/i` → "Incident response"
- `/spend|cost|budget|token|money|bill|\$/i` → "Cost & spend view"
- `/focus|single|drill|deep|zoom|detail/i` → "Focus on active agent"
- `/overnight|last night|yesterday|recap|changed|what happened/i` → "Overnight recap"
- `/approv|review|waiting on me|need (me|you)/i` → "Approvals queue"

Each preset is a complete `layout` array. In production, replace the regex
matcher with an LLM call that returns a layout spec conforming to the same
schema (array of `{ id, gx, gy, w, h }` with `id` ∈ widget registry keys).

## Agent Deep-Dive Overlay
Click-outside or Esc to close. Full-viewport modal on `rgba(4,5,7,0.72)` +
8px backdrop-blur. Inner panel: 1200px max, 86vh, dark gradient header tinted
by agent-type color.
- **Header**: 44px type glyph chip, agent name (22px semibold) + status, task
  text, mono meta row (model · tools · runtime), Pause / Stop / × buttons.
- **Tabs** (brand-colored underline): Timeline & Logs · Artifacts · Sub-agents · Metrics · Context.
  - *Timeline*: 2-col — log stream (same format as Active Agent widget) + step plan checklist.
  - *Artifacts*: files touched with created/modified/read status pills + +/− line counts.
  - *Sub-agents*: spawned sub-processes with status pip and duration.
  - *Metrics*: 2×2 grid of sparkline cards (CPU, token rate, cost, tool calls/min).
  - *Context*: mono render of system prompt + current context-window usage bar.

## Edit Mode
Toggle from header. Applies `.edit-mode` class to grid.
- Widget borders become dashed.
- Drag handle (`⋮⋮`) appears top-left; native HTML5 drag swaps two widgets' positions (`onDragStart/onDragOver/onDrop`, drop target shown with dashed brand outline).
- Remove handle (`−`) appears top-right, red pill.
- "+ Add widget" button in header opens a picker modal listing unplaced widgets with glyph, title, description, and "+" affordance.

## Inline Expand
Each widget has a `⊞ / ⊟` action. Expanded widget promotes to full-width and 3 rows,
gains brand glow, z-index 10. Useful for drill-in without leaving the dashboard.

## Animations
- `pulse` 2s ease-in-out infinite alternate (opacity 1 → 0.4) — status pips, active rings.
- `fadeUp` 300ms — message bubbles, modal entrance.
- `sweep` 2s linear — working Gantt segments.
- Topology packets: 90ms tick, 20 ticks per hop, linear interp along `(x1,y1) → (x2,y2)`.
- Node resize: 400ms `cubic-bezier(.2,.7,.3,1)`.
- Rotating aura on Helm: 2 pulse rings at 2.4s and 3.6s.

## State Management
Suggested shape:
```ts
interface DashboardState {
  layout: { id: WidgetId; gx: number; gy: number; w: number; h: number }[];
  editMode: boolean;
  expanded: WidgetId | null;
  selectedAgent: string;            // drives Fleet/Topology highlight + Active Agent widget
  deepDiveAgent: string | null;
  chatOpen: boolean;
  helmBanner: { title: string; prompt: string } | null;
}
```

Live data (should stream via WebSocket/SSE in production):
```ts
interface Agent {
  id, name, type: 'code'|'research'|'data'|'ops'|'write'|'design',
  status: 'working'|'waiting'|'blocked'|'idle',
  task, progress (0-100), runtime (HH:MM:SS), tokens, cost,
  model, tools: string[]
}
interface TopologyNode {
  id, label, kind: 'helm'|'orc'|'agent',
  agentType?, status?, x (0-100), y (0-100)  // x/y are percent coords
}
interface TopologyEdge { 0: fromId, 1: toId }
```

## Files in this bundle
- `Lord Helm.html` — entry point; hosts 3 variants inside a design canvas.
- `dashboard.jsx` — main `Dashboard` component, header, throne, edit mode, drag, Helm prompt matcher, widget frame/picker.
- `widgets-core.jsx` — FleetRoster, ActiveAgent, **Topology**, KpiGrid, Gantt, Sparkline.
- `widgets-side.jsx` — Alerts, Approvals, Recent, Queue, Health, HelmChat.
- `agent-deep-dive.jsx` — full-screen agent inspection modal.
- `data.jsx` — mock fleet, topology nodes/edges, alerts, approvals, queue, KPIs, health, logs. **Replace with live API/WebSocket subscriptions.**
- `styles/tokens.css` — all design tokens, widget base styles, status pips, keyframes.
- `design-canvas.jsx` — dev-only presentation scaffold; **do not ship**.

## Opening the prototype locally
The HTML uses in-browser Babel (fine for design reference). Open `Lord Helm.html`
via any static file server (`python3 -m http.server`, `npx serve`, etc.) — the
file:// protocol will block the `<script src>` imports.

## Recommended next steps for the implementer
1. Stand up widget primitives (`<Widget>`, `<WidgetHead>`, `<WidgetBody>`) matching the frame spec.
2. Build a grid layout system — react-grid-layout works well and gives drag + resize for free.
3. Wire live data: WebSocket channel pushing agent status/progress/tokens; reducer merges into agent store.
4. Implement Topology with an SVG layer + HTML node layer (as in the reference) — d3-force or static positions both work; the reference uses hand-placed positions.
5. Replace regex preset matcher with an LLM call that returns a `layout[]`.
6. Deep-dive overlay should be its own route (`/agents/:id`) for deep-linking, implemented as a modal that syncs URL state.
