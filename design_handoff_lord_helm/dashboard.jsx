// Lord Helm — main dashboard with throne, edit mode, widget grid, 3 layout variants
const { useState: useStateM, useEffect: useEffectM, useRef: useRefM } = React;

// Preset layouts Lord Helm can "generate" in response to user prompts
const HELM_PRESETS = {
  incident: {
    match: /incident|alert|block|fire|break|fail|error|issue/i,
    title: "Incident response",
    layout: [
      { id: "alerts",    gx: 0, gy: 0, w: 4, h: 3 },
      { id: "topology",  gx: 4, gy: 0, w: 5, h: 3 },
      { id: "active",    gx: 9, gy: 0, w: 3, h: 3 },
      { id: "health",    gx: 0, gy: 3, w: 4, h: 2 },
      { id: "approvals", gx: 4, gy: 3, w: 4, h: 2 },
      { id: "fleet",     gx: 8, gy: 3, w: 4, h: 2 },
    ],
  },
  cost: {
    match: /spend|cost|budget|token|money|bill|\$/i,
    title: "Cost & spend view",
    layout: [
      { id: "kpis",      gx: 0, gy: 0, w: 6, h: 2 },
      { id: "gantt",     gx: 6, gy: 0, w: 6, h: 2 },
      { id: "fleet",     gx: 0, gy: 2, w: 4, h: 3 },
      { id: "active",    gx: 4, gy: 2, w: 5, h: 3 },
      { id: "health",    gx: 9, gy: 2, w: 3, h: 3 },
    ],
  },
  focus: {
    match: /focus|single|drill|deep|zoom|detail/i,
    title: "Focus on active agent",
    layout: [
      { id: "active",    gx: 0, gy: 0, w: 8, h: 5 },
      { id: "fleet",     gx: 8, gy: 0, w: 4, h: 3 },
      { id: "recent",    gx: 8, gy: 3, w: 4, h: 2 },
    ],
  },
  overnight: {
    match: /overnight|last night|yesterday|recap|changed|what happened/i,
    title: "Overnight recap",
    layout: [
      { id: "recent",    gx: 0, gy: 0, w: 5, h: 3 },
      { id: "gantt",     gx: 5, gy: 0, w: 7, h: 3 },
      { id: "kpis",      gx: 0, gy: 3, w: 6, h: 2 },
      { id: "alerts",    gx: 6, gy: 3, w: 6, h: 2 },
    ],
  },
  approval: {
    match: /approv|review|waiting on me|need (me|you)/i,
    title: "Approvals queue",
    layout: [
      { id: "approvals", gx: 0, gy: 0, w: 6, h: 4 },
      { id: "active",    gx: 6, gy: 0, w: 6, h: 4 },
      { id: "fleet",     gx: 0, gy: 4, w: 6, h: 2 },
      { id: "recent",    gx: 6, gy: 4, w: 6, h: 2 },
    ],
  },
};

function matchHelmPreset(prompt) {
  for (const [key, preset] of Object.entries(HELM_PRESETS)) {
    if (preset.match.test(prompt)) return { key, ...preset };
  }
  return null;
}

// Widget registry — maps ID to component + default grid size
const WIDGET_REGISTRY = {
  fleet:     { title: "Fleet Roster",     icon: "♟", component: "FleetRoster",  w: 3, h: 2, desc: "All agents grouped by status" },
  active:    { title: "Active Agent",     icon: "◎", component: "ActiveAgent",  w: 4, h: 2, desc: "Live logs + tool calls" },
  topology:  { title: "Topology",         icon: "⬡", component: "Topology",     w: 5, h: 2, desc: "Network graph of the fleet" },
  kpis:      { title: "KPIs",             icon: "▲", component: "KpiGrid",      w: 3, h: 1, desc: "Throughput, success, spend" },
  gantt:     { title: "Timeline",         icon: "▬", component: "Gantt",        w: 6, h: 2, desc: "Agent work over time" },
  queue:     { title: "Task Queue",       icon: "▤", component: "Queue",        w: 3, h: 2, desc: "Backlog waiting to be picked up" },
  alerts:    { title: "Alerts",           icon: "⚠", component: "Alerts",       w: 3, h: 2, desc: "Incidents & warnings" },
  health:    { title: "System Health",    icon: "◉", component: "Health",       w: 3, h: 1, desc: "CPU, memory, API quotas" },
  approvals: { title: "Approvals",        icon: "✓", component: "Approvals",    w: 3, h: 2, desc: "Actions waiting on you" },
  recent:    { title: "Recent",           icon: "↺", component: "Recent",       w: 3, h: 2, desc: "Completed artifacts & PRs" },
  chat:      { title: "Chat w/ Lord Helm",icon: "♛", component: "HelmChat",     w: 3, h: 3, desc: "Ask the orchestrator anything" },
};

const DEFAULT_LAYOUT = [
  { id: "fleet",     gx: 0, gy: 0, w: 3, h: 3 },
  { id: "topology",  gx: 3, gy: 0, w: 6, h: 3 },
  { id: "active",    gx: 9, gy: 0, w: 3, h: 3 },
  { id: "kpis",      gx: 0, gy: 3, w: 3, h: 2 },
  { id: "gantt",     gx: 3, gy: 3, w: 6, h: 2 },
  { id: "alerts",    gx: 9, gy: 3, w: 3, h: 2 },
  { id: "queue",     gx: 0, gy: 5, w: 3, h: 2 },
  { id: "approvals", gx: 3, gy: 5, w: 3, h: 2 },
  { id: "recent",    gx: 6, gy: 5, w: 3, h: 2 },
  { id: "health",    gx: 9, gy: 5, w: 3, h: 2 },
];

function renderWidget(id, props) {
  const comp = WIDGET_REGISTRY[id]?.component;
  if (!comp) return null;
  const C = window[comp];
  return C ? <C {...props} /> : null;
}

// ---------- Throne: central Lord Helm command input ----------
function Throne({ onOpenChat }) {
  const [input, setInput] = useStateM("");
  const [focused, setFocused] = useStateM(false);
  const examples = [
    "Pause all ops agents",
    "Summarize today's PRs",
    "Who's waiting on approval?",
    "Show me spend this week",
  ];
  const [exIdx, setExIdx] = useStateM(0);
  useEffectM(() => {
    const t = setInterval(() => setExIdx(i => (i + 1) % examples.length), 2800);
    return () => clearInterval(t);
  }, []);

  return (
    <div style={{
      position: "relative",
      borderRadius: 14,
      padding: "14px 18px",
      background: "linear-gradient(135deg, rgba(139, 124, 255, 0.15) 0%, rgba(94, 228, 213, 0.08) 100%), var(--bg-2)",
      border: "1px solid",
      borderColor: focused ? "var(--brand)" : "var(--line-2)",
      boxShadow: focused ? "var(--shadow-glow)" : "var(--shadow-1)",
      transition: "border-color 180ms ease, box-shadow 180ms ease",
    }}>
      <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
        <div style={{
          width: 40, height: 40, borderRadius: 10,
          background: "linear-gradient(135deg, var(--brand-2), var(--brand))",
          display: "grid", placeItems: "center",
          color: "white", fontSize: 20,
          boxShadow: "0 0 24px var(--brand-glow)",
          flexShrink: 0,
        }}>♛</div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 3 }}>
            <span style={{ fontFamily: "var(--font-display)", fontSize: 17, color: "var(--fg-0)", fontStyle: "italic" }}>Lord Helm</span>
            <span className="pip working" />
            <span style={{ fontSize: 10.5, color: "var(--fg-3)", letterSpacing: "0.05em", textTransform: "uppercase" }}>Orchestrating · 12 agents</span>
          </div>
          <div style={{ position: "relative", display: "flex", alignItems: "center", gap: 10 }}>
            <input
              value={input}
              onChange={e => setInput(e.target.value)}
              onFocus={() => setFocused(true)}
              onBlur={() => setFocused(false)}
              onKeyDown={e => { if (e.key === "Enter" && input.trim()) { onOpenChat?.(input); setInput(""); }}}
              placeholder=""
              style={{
                flex: 1, background: "transparent", border: "none", outline: "none",
                color: "var(--fg-0)", fontSize: 15, padding: "4px 0",
                position: "relative", zIndex: 1,
              }}
            />
            {!input && !focused && (
              <div style={{ position: "absolute", left: 0, top: 4, pointerEvents: "none", color: "var(--fg-3)", fontSize: 15, zIndex: 0 }}>
                Command your fleet — <span style={{ color: "var(--fg-2)", fontStyle: "italic" }}>"{examples[exIdx]}"</span>
              </div>
            )}
            <kbd style={{
              fontSize: 10, fontFamily: "var(--font-mono)",
              padding: "2px 6px", borderRadius: 4,
              background: "var(--bg-3)", color: "var(--fg-3)",
              border: "1px solid var(--line-2)",
            }}>⌘K</kbd>
          </div>
        </div>
      </div>
      {/* quick actions */}
      <div style={{ display: "flex", gap: 6, marginTop: 10, flexWrap: "wrap" }}>
        {["Generate dashboard for incident view", "Focus on code agents", "What changed overnight?", "Optimize fleet"].map(s => (
          <button key={s} onClick={() => onOpenChat?.(s)} style={{
            fontSize: 11, padding: "4px 10px", borderRadius: 999,
            background: "var(--bg-3)", color: "var(--fg-2)",
            border: "1px solid var(--line-2)",
            transition: "all 120ms",
          }}
          onMouseEnter={e => { e.currentTarget.style.borderColor = "var(--brand)"; e.currentTarget.style.color = "var(--fg-0)"; }}
          onMouseLeave={e => { e.currentTarget.style.borderColor = "var(--line-2)"; e.currentTarget.style.color = "var(--fg-2)"; }}
          >{s}</button>
        ))}
      </div>
    </div>
  );
}

// ---------- Widget shell (head + body + hover controls) ----------
function WidgetFrame({ id, editMode, expanded, onExpand, onRemove, selectedAgent, onSelectAgent, onDeepDive, style, dragHandlers }) {
  const meta = WIDGET_REGISTRY[id];
  if (!meta) return null;
  const props = {};
  if (id === "fleet") { props.selected = selectedAgent; props.onSelect = onSelectAgent; props.onDeepDive = onDeepDive; }
  if (id === "active") { props.agentId = selectedAgent; props.onDeepDive = onDeepDive; }
  if (id === "topology") { props.selected = selectedAgent; props.onSelectAgent = onSelectAgent; props.onDeepDive = onDeepDive; }

  return (
    <div className={`widget ${expanded ? "expanded" : ""}`} style={style}>
      {editMode && (
        <>
          <button onClick={onRemove} className="remove-handle" title="Remove widget">−</button>
          <div
            {...(dragHandlers || {})}
            style={{
              position: "absolute", top: 8, left: 8, zIndex: 4,
              width: 22, height: 22, borderRadius: 5,
              background: "var(--bg-3)", border: "1px solid var(--line-2)",
              display: "grid", placeItems: "center",
              color: "var(--fg-2)", cursor: "grab", fontSize: 11,
            }}
            title="Drag to reorder"
          >⋮⋮</div>
        </>
      )}
      <div className="widget-head" style={{ paddingLeft: editMode ? 38 : undefined }}>
        <div className="widget-title">
          <span style={{ color: "var(--fg-3)" }}>{meta.icon}</span>
          <span>{meta.title}</span>
        </div>
        <div className="widget-actions">
          <button onClick={onExpand} title={expanded ? "Collapse" : "Expand"}>
            {expanded ? "⊟" : "⊞"}
          </button>
          <button title="Options">⋯</button>
        </div>
      </div>
      <div className="widget-body">
        {renderWidget(id, props)}
      </div>
    </div>
  );
}

// ---------- Add widget picker ----------
function WidgetPicker({ open, onClose, placed, onAdd }) {
  if (!open) return null;
  const available = Object.entries(WIDGET_REGISTRY).filter(([id]) => !placed.includes(id));
  return (
    <div style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)", zIndex: 100, display: "grid", placeItems: "center", animation: "fadeUp 200ms ease" }} onClick={onClose}>
      <div onClick={e => e.stopPropagation()} style={{
        width: 520, maxHeight: "70vh",
        background: "var(--bg-2)",
        border: "1px solid var(--line-2)",
        borderRadius: 14,
        boxShadow: "var(--shadow-2)",
        overflow: "hidden",
        display: "flex", flexDirection: "column",
      }}>
        <div style={{ padding: "14px 18px", borderBottom: "1px solid var(--line-1)", display: "flex", alignItems: "center", justifyContent: "space-between" }}>
          <div>
            <div style={{ fontSize: 14, color: "var(--fg-0)", fontWeight: 600 }}>Add widget</div>
            <div style={{ fontSize: 11, color: "var(--fg-3)", marginTop: 2 }}>Drop into the next open grid cell</div>
          </div>
          <button onClick={onClose} style={{ color: "var(--fg-3)", fontSize: 16 }}>×</button>
        </div>
        <div style={{ overflow: "auto", padding: 8 }}>
          {available.length === 0 && (
            <div style={{ padding: 32, textAlign: "center", color: "var(--fg-3)", fontSize: 13 }}>All widgets are placed. Remove one to add another.</div>
          )}
          {available.map(([id, meta]) => (
            <button key={id} onClick={() => { onAdd(id); onClose(); }} style={{
              display: "grid", gridTemplateColumns: "36px 1fr auto", gap: 12, alignItems: "center",
              width: "100%", textAlign: "left",
              padding: "10px 12px", borderRadius: 8,
              background: "transparent", transition: "background 120ms",
            }}
            onMouseEnter={e => e.currentTarget.style.background = "var(--bg-3)"}
            onMouseLeave={e => e.currentTarget.style.background = "transparent"}
            >
              <div style={{ width: 36, height: 36, borderRadius: 8, background: "var(--bg-3)", display: "grid", placeItems: "center", color: "var(--brand)", fontSize: 16 }}>{meta.icon}</div>
              <div>
                <div style={{ fontSize: 13, color: "var(--fg-0)", fontWeight: 500 }}>{meta.title}</div>
                <div style={{ fontSize: 11, color: "var(--fg-3)" }}>{meta.desc}</div>
              </div>
              <span style={{ fontSize: 18, color: "var(--fg-3)" }}>+</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

// ---------- Dashboard (single variant, configurable) ----------
function Dashboard({ variant = "default" }) {
  const [layout, setLayout] = useStateM(() => {
    if (variant === "compact") {
      return [
        { id: "topology",  gx: 0, gy: 0, w: 7, h: 3 },
        { id: "fleet",     gx: 7, gy: 0, w: 3, h: 5 },
        { id: "chat",      gx: 10, gy: 0, w: 2, h: 5 },
        { id: "active",    gx: 0, gy: 3, w: 4, h: 2 },
        { id: "kpis",      gx: 4, gy: 3, w: 3, h: 2 },
        { id: "alerts",    gx: 0, gy: 5, w: 3, h: 2 },
        { id: "approvals", gx: 3, gy: 5, w: 3, h: 2 },
        { id: "queue",     gx: 6, gy: 5, w: 3, h: 2 },
        { id: "gantt",     gx: 9, gy: 5, w: 3, h: 2 },
      ];
    }
    if (variant === "incident") {
      return [
        { id: "alerts",    gx: 0, gy: 0, w: 4, h: 3 },
        { id: "topology",  gx: 4, gy: 0, w: 5, h: 3 },
        { id: "active",    gx: 9, gy: 0, w: 3, h: 3 },
        { id: "health",    gx: 0, gy: 3, w: 4, h: 2 },
        { id: "approvals", gx: 4, gy: 3, w: 3, h: 2 },
        { id: "fleet",     gx: 7, gy: 3, w: 2, h: 2 },
        { id: "gantt",     gx: 9, gy: 3, w: 3, h: 2 },
        { id: "recent",    gx: 0, gy: 5, w: 3, h: 2 },
        { id: "queue",     gx: 3, gy: 5, w: 3, h: 2 },
        { id: "kpis",      gx: 6, gy: 5, w: 3, h: 2 },
      ];
    }
    return DEFAULT_LAYOUT;
  });

  const [editMode, setEditMode] = useStateM(false);
  const [pickerOpen, setPickerOpen] = useStateM(false);
  const [expanded, setExpanded] = useStateM(null);
  const [selectedAgent, setSelectedAgent] = useStateM("a03");
  const [deepDiveAgent, setDeepDiveAgent] = useStateM(null);
  const [chatOpen, setChatOpen] = useStateM(variant === "compact");
  const [helmBanner, setHelmBanner] = useStateM(null);
  const [dragIdx, setDragIdx] = useStateM(null);
  const [dragOverIdx, setDragOverIdx] = useStateM(null);

  const removeWidget = (id) => setLayout(l => l.filter(w => w.id !== id));
  const addWidget = (id) => {
    const meta = WIDGET_REGISTRY[id];
    const maxY = Math.max(0, ...layout.map(w => w.gy + w.h));
    setLayout(l => [...l, { id, gx: 0, gy: maxY, w: meta.w, h: meta.h }]);
  };
  const placed = layout.map(w => w.id);

  // Helm prompt → layout generation
  const applyHelmPrompt = (prompt) => {
    const preset = matchHelmPreset(prompt);
    if (preset) {
      setLayout(preset.layout);
      setHelmBanner({ title: preset.title, prompt });
      setTimeout(() => setHelmBanner(null), 8000);
    } else {
      setChatOpen(true);
    }
  };

  // Drag reorder handlers
  const handleDragStart = (i) => (e) => {
    setDragIdx(i);
    e.dataTransfer.effectAllowed = "move";
    // Firefox needs this to start a drag
    try { e.dataTransfer.setData("text/plain", String(i)); } catch (_) {}
  };
  const handleDragOver = (i) => (e) => {
    e.preventDefault();
    if (dragIdx !== null && dragIdx !== i) setDragOverIdx(i);
  };
  const handleDrop = (i) => (e) => {
    e.preventDefault();
    if (dragIdx === null || dragIdx === i) { setDragIdx(null); setDragOverIdx(null); return; }
    setLayout(l => {
      const next = [...l];
      // Swap grid positions — keeps sizes intact
      const a = { ...next[dragIdx] }, b = { ...next[i] };
      const swap = { gx: a.gx, gy: a.gy, w: a.w, h: a.h };
      next[dragIdx] = { ...a, gx: b.gx, gy: b.gy, w: b.w, h: b.h };
      next[i]       = { ...b, gx: swap.gx, gy: swap.gy, w: swap.w, h: swap.h };
      return next;
    });
    setDragIdx(null); setDragOverIdx(null);
  };
  const handleDragEnd = () => { setDragIdx(null); setDragOverIdx(null); };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh", background: "var(--bg-0)" }}>
      {/* Top header */}
      <header style={{
        display: "flex", alignItems: "center", gap: 16,
        padding: "12px 20px",
        borderBottom: "1px solid var(--line-1)",
        background: "var(--bg-1)",
        flexShrink: 0,
      }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <div style={{
            width: 28, height: 28, borderRadius: 7,
            background: "linear-gradient(135deg, var(--brand-2), var(--brand))",
            display: "grid", placeItems: "center",
            color: "white", fontSize: 15, fontWeight: 700,
            boxShadow: "0 0 16px var(--brand-glow)",
          }}>♛</div>
          <div>
            <div style={{ fontFamily: "var(--font-display)", fontSize: 17, color: "var(--fg-0)", fontStyle: "italic", lineHeight: 1 }}>Lord Helm</div>
            <div style={{ fontSize: 10, color: "var(--fg-3)", letterSpacing: "0.05em", textTransform: "uppercase", marginTop: 2 }}>Command Center</div>
          </div>
        </div>

        <div style={{ display: "flex", alignItems: "center", gap: 4, marginLeft: 8, color: "var(--fg-3)", fontSize: 12 }}>
          <span>/</span>
          <select style={{ background: "transparent", border: "none", color: "var(--fg-1)", fontSize: 12, outline: "none" }}>
            <option>Production Fleet</option>
            <option>Staging</option>
          </select>
        </div>

        <div style={{ flex: 1 }} />

        {/* live fleet stats */}
        <div style={{ display: "flex", gap: 14, fontSize: 11.5, color: "var(--fg-2)", fontFamily: "var(--font-mono)" }}>
          <span><span className="pip working" style={{ marginRight: 6 }} /> 7 working</span>
          <span><span className="pip waiting" style={{ marginRight: 6 }} /> 2 waiting</span>
          <span><span className="pip blocked" style={{ marginRight: 6 }} /> 2 blocked</span>
          <span><span className="pip idle" style={{ marginRight: 6 }} /> 1 idle</span>
        </div>

        <div style={{ display: "flex", gap: 6, marginLeft: 8 }}>
          <button
            onClick={() => setEditMode(e => !e)}
            style={{
              fontSize: 12, padding: "6px 12px", borderRadius: 6,
              background: editMode ? "var(--brand-2)" : "var(--bg-3)",
              color: editMode ? "white" : "var(--fg-1)",
              border: "1px solid",
              borderColor: editMode ? "var(--brand-2)" : "var(--line-2)",
            }}
          >
            {editMode ? "✓ Done editing" : "✎ Edit layout"}
          </button>
          {editMode && (
            <button onClick={() => setPickerOpen(true)} style={{
              fontSize: 12, padding: "6px 12px", borderRadius: 6,
              background: "var(--bg-3)", color: "var(--fg-1)",
              border: "1px dashed var(--line-3)",
            }}>+ Add widget</button>
          )}
        </div>
      </header>

      {/* Helm-generated layout banner */}
      {helmBanner && (
        <div style={{
          margin: "12px 20px 0", padding: "10px 14px",
          background: "linear-gradient(90deg, rgba(139,124,255,0.18), rgba(94,228,213,0.08))",
          border: "1px solid var(--brand)",
          borderRadius: 10,
          display: "flex", alignItems: "center", gap: 10,
          animation: "fadeUp 260ms ease",
        }}>
          <div style={{ width: 22, height: 22, borderRadius: 6, background: "linear-gradient(135deg, var(--brand-2), var(--brand))", color: "white", display: "grid", placeItems: "center", fontSize: 11, flexShrink: 0 }}>♛</div>
          <div style={{ flex: 1, fontSize: 12, color: "var(--fg-1)" }}>
            <span style={{ color: "var(--fg-0)", fontWeight: 600 }}>Lord Helm rearranged the dashboard.</span>{" "}
            <span style={{ color: "var(--fg-3)" }}>Preset: <span style={{ color: "var(--accent)" }}>{helmBanner.title}</span> · from "{helmBanner.prompt}"</span>
          </div>
          <button onClick={() => { setLayout(DEFAULT_LAYOUT); setHelmBanner(null); }}
            style={{ fontSize: 11, color: "var(--fg-2)", padding: "4px 9px", borderRadius: 5, border: "1px solid var(--line-2)" }}>
            Restore default
          </button>
          <button onClick={() => setHelmBanner(null)} style={{ color: "var(--fg-3)", fontSize: 14, width: 20 }}>×</button>
        </div>
      )}

      {/* Throne row */}
      <div style={{ padding: "14px 20px 0", flexShrink: 0 }}>
        <Throne onOpenChat={(prompt) => { if (prompt) applyHelmPrompt(prompt); else setChatOpen(true); }} />
      </div>

      {/* Widget grid */}
      <div className={editMode ? "edit-mode" : ""} style={{
        flex: 1, minHeight: 0,
        padding: 14,
        overflow: "auto",
      }}>
        <div style={{
          display: "grid",
          gridTemplateColumns: "repeat(12, 1fr)",
          gridAutoRows: "minmax(110px, auto)",
          gap: 10,
        }}>
          {layout.map((w, i) => (
            <div key={w.id} style={{
              gridColumn: `span ${w.w}`,
              gridRow: `span ${w.h}`,
              position: "relative",
              opacity: dragIdx === i ? 0.5 : 1,
              outline: dragOverIdx === i ? "2px dashed var(--brand)" : "none",
              outlineOffset: 2,
              borderRadius: 14,
              transition: "opacity 140ms",
            }}
            onDragOver={editMode ? handleDragOver(i) : undefined}
            onDrop={editMode ? handleDrop(i) : undefined}
            >
              <WidgetFrame
                id={w.id}
                editMode={editMode}
                expanded={expanded === w.id}
                onExpand={() => setExpanded(e => e === w.id ? null : w.id)}
                onRemove={() => removeWidget(w.id)}
                selectedAgent={selectedAgent}
                onSelectAgent={setSelectedAgent}
                onDeepDive={setDeepDiveAgent}
                dragHandlers={editMode ? {
                  draggable: true,
                  onDragStart: handleDragStart(i),
                  onDragEnd: handleDragEnd,
                } : null}
                style={{ height: "100%" }}
              />
            </div>
          ))}
        </div>
      </div>

      {/* Floating chat panel */}
      {chatOpen && (
        <div style={{
          position: "fixed",
          bottom: 20, right: 20,
          width: 380, height: 520,
          background: "var(--bg-2)",
          border: "1px solid var(--line-2)",
          borderRadius: 14,
          boxShadow: "var(--shadow-2), 0 0 48px rgba(139,124,255,0.2)",
          display: "flex", flexDirection: "column",
          zIndex: 50,
          animation: "fadeUp 200ms ease",
        }}>
          <div style={{ display: "flex", alignItems: "center", gap: 10, padding: "12px 14px", borderBottom: "1px solid var(--line-1)" }}>
            <div style={{ width: 24, height: 24, borderRadius: 6, background: "linear-gradient(135deg, var(--brand-2), var(--brand))", color: "white", display: "grid", placeItems: "center", fontSize: 12 }}>♛</div>
            <span style={{ fontFamily: "var(--font-display)", fontSize: 15, color: "var(--fg-0)", fontStyle: "italic" }}>Lord Helm</span>
            <span style={{ fontSize: 10, color: "var(--fg-3)" }}>· commanding</span>
            <div style={{ flex: 1 }} />
            <button onClick={() => setChatOpen(false)} style={{ color: "var(--fg-3)", fontSize: 16 }}>×</button>
          </div>
          <div style={{ flex: 1, minHeight: 0 }}>
            <HelmChat />
          </div>
        </div>
      )}

      <WidgetPicker open={pickerOpen} onClose={() => setPickerOpen(false)} placed={placed} onAdd={addWidget} />
      {deepDiveAgent && <AgentDeepDive agentId={deepDiveAgent} onClose={() => setDeepDiveAgent(null)} />}
    </div>
  );
}

Object.assign(window, { Dashboard, Throne, WidgetFrame, WidgetPicker, WIDGET_REGISTRY, DEFAULT_LAYOUT });
