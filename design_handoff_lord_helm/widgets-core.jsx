// Lord Helm — core widgets (fleet, active agent, topology, gantt, kpis)
const { useState, useEffect, useRef, useMemo } = React;

// ---------- shared sparkline ----------
function Sparkline({ data, color = "var(--brand)", height = 24, fill = true }) {
  const w = 100, h = height;
  const min = Math.min(...data), max = Math.max(...data);
  const range = max - min || 1;
  const points = data.map((v, i) => {
    const x = (i / (data.length - 1)) * w;
    const y = h - ((v - min) / range) * (h - 2) - 1;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  const area = `0,${h} ${points} ${w},${h}`;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ width: "100%", height, display: "block" }}>
      {fill && <polygon points={area} fill={color} opacity="0.15" />}
      <polyline points={points} fill="none" stroke={color} strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

// ---------- Fleet Roster ----------
function FleetRoster({ selected, onSelect, onDeepDive }) {
  const groups = useMemo(() => {
    const out = { working: [], waiting: [], blocked: [], idle: [] };
    AGENTS.forEach(a => out[a.status].push(a));
    return out;
  }, []);
  const order = [
    { key: "working", label: "Working" },
    { key: "waiting", label: "Waiting" },
    { key: "blocked", label: "Blocked" },
    { key: "idle",    label: "Idle" },
  ];
  return (
    <div style={{ padding: "6px 0" }}>
      {order.map(g => groups[g.key].length > 0 && (
        <div key={g.key} style={{ marginBottom: 6 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 14px", color: "var(--fg-3)", fontSize: 10.5, letterSpacing: "0.08em", textTransform: "uppercase", fontWeight: 600 }}>
            <span className={`pip ${g.key}`} />
            <span>{g.label}</span>
            <span style={{ color: "var(--fg-4)" }}>· {groups[g.key].length}</span>
          </div>
          {groups[g.key].map(a => (
            <button
              key={a.id}
              onClick={() => onSelect?.(a.id)}
              onDoubleClick={() => onDeepDive?.(a.id)}
              title="Click to select · double-click to deep-dive"
              style={{
                display: "grid",
                gridTemplateColumns: "22px 1fr auto",
                alignItems: "center",
                gap: 10,
                width: "100%",
                padding: "7px 14px",
                textAlign: "left",
                background: selected === a.id ? "var(--bg-3)" : "transparent",
                borderLeft: `2px solid ${selected === a.id ? AGENT_COLORS[a.type] : "transparent"}`,
                transition: "background 120ms ease",
              }}
              onMouseEnter={e => { if (selected !== a.id) e.currentTarget.style.background = "var(--bg-2)"; }}
              onMouseLeave={e => { if (selected !== a.id) e.currentTarget.style.background = "transparent"; }}
            >
              <span style={{ width: 22, height: 22, borderRadius: 6, background: `color-mix(in oklab, ${AGENT_COLORS[a.type]} 18%, transparent)`, color: AGENT_COLORS[a.type], display: "grid", placeItems: "center", fontSize: 11, fontWeight: 700 }}>
                {AGENT_GLYPHS[a.type]}
              </span>
              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: 13, color: "var(--fg-0)", fontWeight: 500 }}>{a.name}</div>
                <div style={{ fontSize: 11, color: "var(--fg-3)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                  {a.status === "idle" ? "standby" : a.task}
                </div>
              </div>
              {a.status === "working" && <div style={{ fontSize: 10, color: "var(--fg-3)", fontVariantNumeric: "tabular-nums" }}>{a.progress}%</div>}
            </button>
          ))}
        </div>
      ))}
    </div>
  );
}

// ---------- Active Agent Detail ----------
function ActiveAgent({ agentId, onDeepDive }) {
  const agent = AGENTS.find(a => a.id === agentId) || AGENTS[2];
  const logs = AGENT_LOGS[agent.id] || AGENT_LOGS.a03;
  const color = AGENT_COLORS[agent.type];
  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <div style={{ padding: "14px 14px 12px", borderBottom: "1px solid var(--line-1)" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }}>
          <div style={{ width: 32, height: 32, borderRadius: 8, background: `color-mix(in oklab, ${color} 22%, transparent)`, color, display: "grid", placeItems: "center", fontSize: 15, fontWeight: 700 }}>
            {AGENT_GLYPHS[agent.type]}
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span style={{ fontSize: 14, color: "var(--fg-0)", fontWeight: 600 }}>{agent.name}</span>
              <span className={`pip ${agent.status}`} />
              <span style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "capitalize" }}>{agent.status}</span>
            </div>
            <div style={{ fontSize: 11, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>{agent.model} · {agent.tools.join(" · ")}</div>
          </div>
          <button onClick={() => onDeepDive?.(agent.id)} title="Open deep-dive (double-click works too)"
            style={{ fontSize: 11, padding: "4px 9px", borderRadius: 5, background: "var(--bg-3)", color: "var(--fg-2)", border: "1px solid var(--line-2)" }}>
            ⤢ Deep dive
          </button>
        </div>
        <div style={{ fontSize: 13, color: "var(--fg-1)", marginBottom: 10, lineHeight: 1.4 }}>{agent.task}</div>
        {/* progress */}
        <div style={{ height: 4, background: "var(--bg-3)", borderRadius: 999, overflow: "hidden", marginBottom: 10 }}>
          <div style={{ width: `${agent.progress}%`, height: "100%", background: color, transition: "width 400ms ease" }} />
        </div>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 8 }}>
          {[
            ["Runtime", agent.runtime],
            ["Tokens", agent.tokens > 1000 ? `${(agent.tokens/1000).toFixed(1)}k` : agent.tokens],
            ["Cost",   `$${agent.cost.toFixed(2)}`],
            ["Progress", `${agent.progress}%`],
          ].map(([k, v]) => (
            <div key={k}>
              <div style={{ fontSize: 9.5, color: "var(--fg-3)", letterSpacing: "0.06em", textTransform: "uppercase", marginBottom: 2 }}>{k}</div>
              <div style={{ fontSize: 13, color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontVariantNumeric: "tabular-nums" }}>{v}</div>
            </div>
          ))}
        </div>
      </div>
      {/* log stream */}
      <div style={{ flex: 1, overflow: "auto", padding: "10px 14px", fontFamily: "var(--font-mono)", fontSize: 11.5, lineHeight: 1.6 }}>
        {logs.map((l, i) => {
          const lvlColor = l.lvl === "tool" ? "var(--brand)" : l.lvl === "out" ? "var(--ok)" : l.lvl === "think" ? "var(--fg-3)" : "var(--fg-2)";
          return (
            <div key={i} style={{ display: "grid", gridTemplateColumns: "56px 48px 1fr", gap: 8, padding: "2px 0", color: "var(--fg-2)", animation: `fadeUp 300ms ease ${i * 30}ms both` }}>
              <span style={{ color: "var(--fg-4)" }}>{l.t}</span>
              <span style={{ color: lvlColor, textTransform: "uppercase", fontSize: 10, letterSpacing: "0.05em" }}>{l.lvl}</span>
              <span style={{ color: l.lvl === "think" ? "var(--fg-3)" : "var(--fg-1)", fontStyle: l.lvl === "think" ? "italic" : "normal" }}>{l.txt}</span>
            </div>
          );
        })}
        {agent.status === "working" && (
          <div style={{ display: "grid", gridTemplateColumns: "56px 48px 1fr", gap: 8, padding: "2px 0" }}>
            <span style={{ color: "var(--fg-4)" }}>{agent.runtime}</span>
            <span style={{ color: "var(--fg-3)", fontSize: 10, textTransform: "uppercase" }}>···</span>
            <span style={{ color: "var(--fg-3)" }}>
              <span style={{ display: "inline-block", width: 6, height: 12, background: color, animation: "pulse 1s ease-in-out infinite", verticalAlign: "middle" }} />
            </span>
          </div>
        )}
      </div>
    </div>
  );
}

// ---------- Topology (dynamic — nodes scale with workload, animated packets) ----------
function Topology({ onSelectAgent, selected, onDeepDive }) {
  const [hover, setHover] = useState(null);
  const [tick, setTick] = useState(0);
  const [packets, setPackets] = useState([]);
  const nodeById = useMemo(() => Object.fromEntries(TOPOLOGY_NODES.map(n => [n.id, n])), []);

  // Compute workload-driven size per node. Agents scale with progress/tokens; pods aggregate children; helm aggregates everything.
  const nodeMetrics = useMemo(() => {
    const agentMap = Object.fromEntries(AGENTS.map(a => [a.id, a]));
    const metrics = {};
    // Agents
    TOPOLOGY_NODES.forEach(n => {
      if (n.kind === "agent") {
        const a = agentMap[n.id];
        // load = progress% blended with token usage (cap 200k)
        const load = a && a.status === "working"
          ? Math.min(1, (a.progress / 100) * 0.6 + Math.min(a.tokens / 200000, 1) * 0.4)
          : a && a.status === "blocked" ? 0.35
          : a && a.status === "waiting" ? 0.18
          : 0.08;
        metrics[n.id] = { load, status: a?.status || n.status, agent: a };
      }
    });
    // Pod nodes: aggregate child load
    const podChildren = {};
    TOPOLOGY_EDGES.forEach(([p, c]) => {
      if (nodeById[p]?.kind === "orc") (podChildren[p] ||= []).push(c);
    });
    TOPOLOGY_NODES.filter(n => n.kind === "orc").forEach(n => {
      const children = podChildren[n.id] || [];
      const sum = children.reduce((acc, id) => acc + (metrics[id]?.load || 0), 0);
      metrics[n.id] = { load: Math.min(1, sum / Math.max(1, children.length * 0.8)) };
    });
    // Helm: total fleet load
    const totalLoad = Object.values(metrics).filter((_, i, arr) => true).reduce((a, m) => a + (m.load || 0), 0);
    metrics.helm = { load: Math.min(1, totalLoad / 10) };
    return metrics;
  }, [nodeById]);

  // Emit packets periodically — from working agents up to pods → helm (telemetry), and occasionally helm → agent (instructions)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 90);
    return () => clearInterval(id);
  }, []);

  useEffect(() => {
    // Every couple ticks, spawn a new packet
    if (tick % 4 !== 0) return;
    const newPackets = [];
    const agentToPod = {};
    TOPOLOGY_EDGES.forEach(([p, c]) => {
      if (nodeById[p]?.kind === "orc" && nodeById[c]?.kind === "agent") agentToPod[c] = p;
    });

    TOPOLOGY_NODES.forEach(n => {
      if (n.kind !== "agent") return;
      const m = nodeMetrics[n.id];
      if (!m) return;
      // working agents frequently send telemetry up; blocked occasionally send alert packets
      const shouldSend = (m.status === "working" && Math.random() < 0.55)
                     || (m.status === "blocked" && Math.random() < 0.25);
      if (!shouldSend) return;
      const pod = agentToPod[n.id];
      if (!pod) return;
      // Two-hop path: agent → pod → helm
      newPackets.push({
        id: `${n.id}-${tick}-${Math.random().toString(36).slice(2,6)}`,
        from: n.id, to: pod, next: "helm",
        kind: m.status === "blocked" ? "alert" : "telemetry",
        color: AGENT_COLORS[n.agentType],
        bornAt: tick,
        duration: 20, // ticks per hop
      });
    });

    // Helm → agents: command packets occasionally
    if (Math.random() < 0.35) {
      const targets = TOPOLOGY_NODES.filter(n => n.kind === "orc");
      const pod = targets[Math.floor(Math.random() * targets.length)];
      if (pod) {
        newPackets.push({
          id: `cmd-${tick}-${Math.random().toString(36).slice(2,6)}`,
          from: "helm", to: pod.id, next: null,
          kind: "command",
          color: "var(--brand)",
          bornAt: tick,
          duration: 18,
        });
      }
    }

    if (newPackets.length) setPackets(ps => [...ps, ...newPackets]);

    // GC packets that have finished both hops
    setPackets(ps => ps.filter(p => (tick - p.bornAt) < (p.next ? p.duration * 2 + 4 : p.duration + 4)));
  }, [tick, nodeMetrics, nodeById]);

  const getNodePos = (id) => {
    const n = nodeById[id];
    return n ? { x: n.x, y: n.y } : { x: 50, y: 50 };
  };

  return (
    <div style={{ position: "relative", width: "100%", height: "100%", background: "radial-gradient(circle at 50% 50%, rgba(139, 124, 255, 0.1) 0%, transparent 55%)", overflow: "hidden" }}>
      {/* grid backdrop */}
      <svg width="100%" height="100%" style={{ position: "absolute", inset: 0, pointerEvents: "none" }}>
        <defs>
          <pattern id="topo-grid" width="28" height="28" patternUnits="userSpaceOnUse">
            <path d="M 28 0 L 0 0 0 28" fill="none" stroke="var(--line-1)" strokeWidth="1"/>
          </pattern>
          <radialGradient id="helm-glow">
            <stop offset="0%" stopColor="rgba(139,124,255,0.35)" />
            <stop offset="100%" stopColor="rgba(139,124,255,0)" />
          </radialGradient>
        </defs>
        <rect width="100%" height="100%" fill="url(#topo-grid)" opacity="0.5" />
        <circle cx="50%" cy="50%" r="18%" fill="url(#helm-glow)" />
      </svg>

      {/* edges + packets (single SVG, aspect-filled) */}
      <svg width="100%" height="100%" viewBox="0 0 100 100" preserveAspectRatio="none"
        style={{ position: "absolute", inset: 0, pointerEvents: "none" }}>
        {TOPOLOGY_EDGES.map(([a, b], i) => {
          const na = nodeById[a], nb = nodeById[b];
          if (!na || !nb) return null;
          const mb = nodeMetrics[b];
          const active = mb && mb.load > 0.2;
          const blocked = nb.status === "blocked";
          const stroke = blocked ? "rgba(248,113,113,0.45)"
                       : active ? `rgba(94, 228, 213, ${0.25 + (mb.load || 0) * 0.4})`
                       : "rgba(139, 124, 255, 0.12)";
          return (
            <line key={i} x1={na.x} y1={na.y} x2={nb.x} y2={nb.y}
              stroke={stroke}
              strokeWidth={active ? 0.22 : 0.14}
              strokeDasharray={active ? "0.4 0.5" : "none"}
              vectorEffect="non-scaling-stroke"
            >
              {active && <animate attributeName="stroke-dashoffset" from="0" to="-1.8" dur="1.2s" repeatCount="indefinite" />}
            </line>
          );
        })}

        {/* animated packets */}
        {packets.map(p => {
          const age = tick - p.bornAt;
          let t, from, to;
          if (age <= p.duration) { t = age / p.duration; from = p.from; to = p.to; }
          else if (p.next && age <= p.duration * 2) { t = (age - p.duration) / p.duration; from = p.to; to = p.next; }
          else return null;
          const fp = getNodePos(from), tp = getNodePos(to);
          const x = fp.x + (tp.x - fp.x) * t;
          const y = fp.y + (tp.y - fp.y) * t;
          const packetColor = p.kind === "alert" ? "#f87171" : p.kind === "command" ? "var(--brand)" : p.color;
          return (
            <g key={p.id}>
              {/* trail */}
              <circle cx={x} cy={y} r="1.6" fill={packetColor} opacity="0.15" />
              <circle cx={x} cy={y} r="0.9" fill={packetColor} opacity="0.35" />
              <circle cx={x} cy={y} r="0.5" fill={packetColor} />
            </g>
          );
        })}
      </svg>

      {/* nodes */}
      {TOPOLOGY_NODES.map(n => {
        const isHelm = n.kind === "helm";
        const isOrc = n.kind === "orc";
        const color = n.kind === "agent" ? AGENT_COLORS[n.agentType] : isHelm ? "var(--brand)" : "var(--accent)";
        const m = nodeMetrics[isHelm ? "helm" : n.id] || { load: 0 };
        const isActive = n.status === "working";
        const isBlocked = n.status === "blocked";
        const isSelected = selected === n.id;

        // Dynamic size: base + load factor
        const baseSize = isHelm ? 56 : isOrc ? 34 : 22;
        const maxGrow  = isHelm ? 24 : isOrc ? 20 : 18;
        const size = baseSize + m.load * maxGrow;

        // Pulse ring opacity scales with load
        const pulseOpacity = 0.2 + m.load * 0.5;

        return (
          <button key={n.id}
            onClick={() => n.kind === "agent" && onSelectAgent?.(n.id)}
            onDoubleClick={() => n.kind === "agent" && onDeepDive?.(n.id)}
            onMouseEnter={() => setHover(n.id)}
            onMouseLeave={() => setHover(null)}
            style={{
              position: "absolute",
              left: `${n.x}%`, top: `${n.y}%`,
              transform: "translate(-50%, -50%)",
              width: size, height: size,
              borderRadius: isHelm ? 12 : 999,
              background: isHelm
                ? "linear-gradient(135deg, var(--brand-2), var(--brand))"
                : `radial-gradient(circle at 30% 30%, color-mix(in oklab, ${color} 40%, transparent), color-mix(in oklab, ${color} 12%, var(--bg-1)))`,
              border: `1.5px solid ${color}`,
              boxShadow: isHelm
                ? `0 0 ${30 + m.load * 50}px var(--brand-glow), 0 0 0 ${2 + m.load * 4}px rgba(139,124,255,0.15)`
                : isActive
                  ? `0 0 0 ${Math.round(2 + m.load * 6)}px color-mix(in oklab, ${color} ${Math.round(10 + m.load * 30)}%, transparent)`
                  : "none",
              color,
              display: "grid", placeItems: "center",
              fontSize: isHelm ? 24 : isOrc ? 14 : 11,
              fontWeight: 700,
              cursor: n.kind === "agent" ? "pointer" : "default",
              zIndex: isHelm ? 5 : isOrc ? 3 : 2,
              outline: isSelected ? `2px solid var(--accent)` : "none",
              outlineOffset: 3,
              transition: "width 400ms cubic-bezier(.2,.7,.3,1), height 400ms cubic-bezier(.2,.7,.3,1), box-shadow 400ms ease",
            }}
          >
            {isHelm ? "♛" : isOrc ? "◉" : AGENT_GLYPHS[n.agentType]}

            {/* load ring for agents + pods */}
            {!isHelm && m.load > 0.1 && (
              <svg style={{ position: "absolute", inset: -4, pointerEvents: "none" }} viewBox="0 0 100 100">
                <circle cx="50" cy="50" r="48" fill="none" stroke={color} strokeWidth="2" strokeDasharray={`${m.load * 301} 301`}
                  strokeLinecap="round" opacity="0.7" transform="rotate(-90 50 50)" />
              </svg>
            )}

            {/* pulse ring */}
            {isActive && !isHelm && (
              <span style={{ position: "absolute", inset: -6, borderRadius: "inherit", border: `1px solid ${color}`, opacity: pulseOpacity, animation: "pulse 1.8s ease-in-out infinite" }} />
            )}

            {/* helm rotating aura */}
            {isHelm && (
              <>
                <span style={{ position: "absolute", inset: -10, borderRadius: 14, border: "1px solid rgba(139,124,255,0.4)", animation: "pulse 2.4s ease-in-out infinite" }} />
                <span style={{ position: "absolute", inset: -18, borderRadius: 18, border: "1px dashed rgba(139,124,255,0.2)", animation: "pulse 3.6s ease-in-out infinite" }} />
              </>
            )}

            {/* blocked marker */}
            {isBlocked && (
              <span style={{ position: "absolute", top: -3, right: -3, width: 10, height: 10, borderRadius: 999, background: "var(--err)", boxShadow: "0 0 0 2px var(--bg-1), 0 0 8px var(--err)" }} />
            )}

            {/* load label under helm/pods */}
            {(isHelm || isOrc) && (
              <div style={{
                position: "absolute", top: "calc(100% + 6px)", left: "50%", transform: "translateX(-50%)",
                fontSize: isHelm ? 11 : 9, color: "var(--fg-2)", fontFamily: "var(--font-mono)", fontWeight: 500,
                whiteSpace: "nowrap", pointerEvents: "none",
              }}>
                {isHelm ? "Lord Helm" : n.label}
                <span style={{ color: "var(--fg-4)", marginLeft: 4 }}>· {Math.round(m.load * 100)}%</span>
              </div>
            )}
          </button>
        );
      })}

      {/* hover tooltip */}
      {hover && (() => {
        const n = nodeById[hover];
        const m = nodeMetrics[hover === "helm" ? "helm" : n.id] || { load: 0 };
        const ag = m.agent;
        return (
          <div style={{
            position: "absolute",
            left: `${n.x}%`, top: `calc(${n.y}% + ${38}px)`,
            transform: "translateX(-50%)",
            background: "var(--bg-3)",
            border: "1px solid var(--line-2)",
            borderRadius: 8,
            padding: "8px 10px",
            fontSize: 11,
            color: "var(--fg-0)",
            whiteSpace: "nowrap",
            pointerEvents: "none",
            zIndex: 20,
            boxShadow: "var(--shadow-2)",
            minWidth: 160,
          }}>
            <div style={{ fontWeight: 600, marginBottom: 3 }}>{n.label}</div>
            <div style={{ color: "var(--fg-3)", fontFamily: "var(--font-mono)", fontSize: 10 }}>
              load · {Math.round(m.load * 100)}%
              {ag && <> · {ag.tokens > 1000 ? `${(ag.tokens/1000).toFixed(0)}k` : ag.tokens} tok</>}
            </div>
            {ag && <div style={{ color: "var(--fg-2)", marginTop: 4, fontSize: 10, whiteSpace: "normal", maxWidth: 200 }}>{ag.task}</div>}
            {n.kind === "agent" && <div style={{ color: "var(--fg-4)", marginTop: 4, fontSize: 9 }}>dbl-click for deep dive</div>}
          </div>
        );
      })()}

      {/* legend */}
      <div style={{ position: "absolute", bottom: 10, left: 12, display: "flex", gap: 12, flexWrap: "wrap", fontSize: 10, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>
        <span style={{ display: "flex", alignItems: "center", gap: 5 }}><span style={{ width: 6, height: 6, borderRadius: 999, background: "var(--accent)" }} />telemetry</span>
        <span style={{ display: "flex", alignItems: "center", gap: 5 }}><span style={{ width: 6, height: 6, borderRadius: 999, background: "var(--brand)" }} />command</span>
        <span style={{ display: "flex", alignItems: "center", gap: 5 }}><span style={{ width: 6, height: 6, borderRadius: 999, background: "var(--err)" }} />alert</span>
        <span style={{ color: "var(--fg-4)", marginLeft: 6 }}>node size = workload</span>
      </div>
    </div>
  );
}

// ---------- KPIs ----------
function KpiGrid() {
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(2, 1fr)", gap: 1, background: "var(--line-1)" }}>
      {KPIS.map(k => {
        const positive = k.delta.startsWith("+");
        const isSpend = k.label.includes("Spend");
        const deltaColor = isSpend ? (positive ? "var(--err)" : "var(--ok)") : (positive ? "var(--ok)" : "var(--err)");
        return (
          <div key={k.label} style={{ background: "var(--bg-1)", padding: "14px 16px" }}>
            <div style={{ fontSize: 10.5, color: "var(--fg-3)", letterSpacing: "0.08em", textTransform: "uppercase", fontWeight: 600, marginBottom: 6 }}>{k.label}</div>
            <div style={{ display: "flex", alignItems: "baseline", gap: 8, marginBottom: 6 }}>
              <span style={{ fontSize: 22, color: "var(--fg-0)", fontWeight: 600, fontVariantNumeric: "tabular-nums", letterSpacing: "-0.01em" }}>{k.value}</span>
              <span style={{ fontSize: 11, color: deltaColor, fontVariantNumeric: "tabular-nums" }}>{k.delta}</span>
            </div>
            <Sparkline data={k.trend} color={deltaColor} height={22} />
          </div>
        );
      })}
    </div>
  );
}

// ---------- Gantt / Timeline ----------
function Gantt() {
  const hours = 6;
  const ticks = Array.from({ length: hours + 1 }, (_, i) => i);
  // Fake per-agent timeline segments
  const bars = [
    { name: "Scribe-7",     type: "write",    segs: [[0.2, 1.8, "done"], [2.0, 3.4, "working"]] },
    { name: "Forgemaster",  type: "code",     segs: [[0.1, 1.2, "done"], [1.4, 2.8, "done"], [3.0, 4.9, "working"]] },
    { name: "Cartographer", type: "research", segs: [[1.2, 2.1, "done"], [2.3, 4.1, "working"]] },
    { name: "Oracle",       type: "data",     segs: [[0.0, 2.4, "done"], [2.6, 4.8, "working"]] },
    { name: "Anvil",        type: "code",     segs: [[0.8, 2.6, "done"], [2.9, 4.7, "working"]] },
    { name: "Loom",         type: "design",   segs: [[1.9, 3.2, "done"], [3.4, 4.3, "working"]] },
    { name: "Ledger",       type: "data",     segs: [[2.1, 3.0, "done"], [3.2, 4.6, "working"]] },
    { name: "Sentinel",     type: "ops",      segs: [[0.4, 1.5, "done"], [1.7, 4.2, "blocked"]] },
  ];
  const now = 4.9;
  return (
    <div style={{ padding: "10px 14px", display: "flex", flexDirection: "column", gap: 6, minHeight: "100%" }}>
      {/* header ticks */}
      <div style={{ display: "grid", gridTemplateColumns: "92px 1fr", alignItems: "center", color: "var(--fg-4)", fontSize: 10, fontFamily: "var(--font-mono)" }}>
        <div />
        <div style={{ position: "relative", height: 14 }}>
          {ticks.map(t => (
            <div key={t} style={{ position: "absolute", left: `${(t/hours)*100}%`, transform: "translateX(-50%)", top: 0 }}>
              −{hours - t}h
            </div>
          ))}
        </div>
      </div>
      {bars.map((b, i) => (
        <div key={i} style={{ display: "grid", gridTemplateColumns: "92px 1fr", alignItems: "center", gap: 8, fontSize: 11 }}>
          <div style={{ color: "var(--fg-2)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
            <span style={{ color: AGENT_COLORS[b.type], marginRight: 6 }}>{AGENT_GLYPHS[b.type]}</span>{b.name}
          </div>
          <div style={{ position: "relative", height: 16, background: "var(--bg-2)", borderRadius: 4, overflow: "hidden" }}>
            {/* hour gridlines */}
            {ticks.slice(1, -1).map(t => (
              <div key={t} style={{ position: "absolute", left: `${(t/hours)*100}%`, top: 0, bottom: 0, width: 1, background: "var(--line-1)" }} />
            ))}
            {b.segs.map((s, j) => {
              const [start, end, kind] = s;
              const left = (start / hours) * 100;
              const width = ((end - start) / hours) * 100;
              const color = kind === "done" ? "color-mix(in oklab, " + AGENT_COLORS[b.type] + " 40%, var(--bg-3))"
                         : kind === "working" ? AGENT_COLORS[b.type]
                         : "var(--err)";
              return (
                <div key={j} style={{
                  position: "absolute",
                  left: `${left}%`, width: `${width}%`,
                  top: 2, bottom: 2,
                  background: color,
                  borderRadius: 3,
                  overflow: "hidden",
                }}>
                  {kind === "working" && (
                    <div style={{ position: "absolute", inset: 0, background: "linear-gradient(90deg, transparent, rgba(255,255,255,0.25), transparent)", animation: "sweep 2s linear infinite" }} />
                  )}
                </div>
              );
            })}
            {/* now line */}
            <div style={{ position: "absolute", left: `${(now/hours)*100}%`, top: -2, bottom: -2, width: 1, background: "var(--accent)", boxShadow: "0 0 6px var(--accent)" }} />
          </div>
        </div>
      ))}
    </div>
  );
}

Object.assign(window, { FleetRoster, ActiveAgent, Topology, KpiGrid, Gantt, Sparkline });
