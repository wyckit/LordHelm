// Agent deep-dive overlay — full detail on a single agent
const { useState: useStateAD, useEffect: useEffectAD } = React;

function AgentDeepDive({ agentId, onClose }) {
  const agent = AGENTS.find(a => a.id === agentId);
  if (!agent) return null;
  const color = AGENT_COLORS[agent.type];
  const logs = AGENT_LOGS[agent.id] || AGENT_LOGS.a03;

  const [tab, setTab] = useStateAD("timeline");

  useEffectAD(() => {
    const onKey = (e) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Fake metric history for this agent
  const cpuTrend = [12, 18, 15, 22, 28, 24, 30, 35, 31, 38, 42, 40];
  const tokenTrend = [800, 1200, 900, 1600, 2100, 1800, 2400, 2800, 2600, 3100, 3400, 3200];

  // Fake file/artifact touch list
  const artifacts = [
    { path: "src/auth/middleware.ts",     status: "modified", lines: "+42 −18" },
    { path: "src/auth/session.ts",        status: "read",     lines: "—" },
    { path: "tests/auth/middleware.test", status: "modified", lines: "+24 −0" },
    { path: "docs/auth/flow.md",          status: "created",  lines: "+38" },
  ];

  const subagents = [
    { name: "test-runner",   status: "done",    duration: "2.4s" },
    { name: "lint-fixer",    status: "done",    duration: "0.8s" },
    { name: "pr-writer",     status: "working", duration: "—" },
  ];

  return (
    <div
      onClick={onClose}
      style={{
        position: "fixed", inset: 0,
        background: "rgba(4, 5, 7, 0.72)",
        backdropFilter: "blur(8px)",
        zIndex: 200,
        display: "grid", placeItems: "center",
        padding: 24,
        animation: "fadeUp 220ms ease",
      }}
    >
      <div onClick={e => e.stopPropagation()} style={{
        width: "min(1200px, 100%)",
        height: "min(86vh, 860px)",
        background: "var(--bg-1)",
        border: "1px solid var(--line-2)",
        borderRadius: 16,
        boxShadow: "0 40px 80px -20px rgba(0,0,0,0.6), 0 0 0 1px var(--line-2)",
        overflow: "hidden",
        display: "flex", flexDirection: "column",
      }}>
        {/* header */}
        <div style={{
          padding: "18px 24px 16px",
          borderBottom: "1px solid var(--line-1)",
          display: "flex", alignItems: "flex-start", gap: 16,
          background: `linear-gradient(180deg, color-mix(in oklab, ${color} 10%, transparent), transparent)`,
        }}>
          <div style={{
            width: 44, height: 44, borderRadius: 10,
            background: `color-mix(in oklab, ${color} 22%, transparent)`,
            color, display: "grid", placeItems: "center",
            fontSize: 20, fontWeight: 700,
            border: `1px solid ${color}`,
          }}>{AGENT_GLYPHS[agent.type]}</div>
          <div style={{ flex: 1 }}>
            <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 4 }}>
              <span style={{ fontSize: 22, color: "var(--fg-0)", fontWeight: 600, letterSpacing: "-0.01em" }}>{agent.name}</span>
              <span className={`pip ${agent.status}`} />
              <span style={{ fontSize: 12, color: "var(--fg-2)", textTransform: "capitalize" }}>{agent.status}</span>
              <span style={{ fontSize: 11, color: "var(--fg-4)", fontFamily: "var(--font-mono)", marginLeft: 8 }}>id: {agent.id}</span>
            </div>
            <div style={{ fontSize: 14, color: "var(--fg-1)", marginBottom: 10 }}>{agent.task}</div>
            <div style={{ display: "flex", gap: 20, fontSize: 11, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>
              <span>model · <span style={{ color: "var(--fg-1)" }}>{agent.model}</span></span>
              <span>tools · <span style={{ color: "var(--fg-1)" }}>{agent.tools.join(", ")}</span></span>
              <span>runtime · <span style={{ color: "var(--fg-1)" }}>{agent.runtime}</span></span>
            </div>
          </div>
          <div style={{ display: "flex", gap: 8 }}>
            <button style={{ fontSize: 12, padding: "7px 12px", borderRadius: 6, background: "var(--bg-3)", color: "var(--fg-1)", border: "1px solid var(--line-2)" }}>Pause</button>
            <button style={{ fontSize: 12, padding: "7px 12px", borderRadius: 6, background: "var(--err-bg)", color: "var(--err)", border: "1px solid var(--err)" }}>Stop</button>
            <button onClick={onClose} style={{ width: 30, height: 30, borderRadius: 6, color: "var(--fg-3)", fontSize: 18 }}>×</button>
          </div>
        </div>

        {/* tabs */}
        <div style={{ display: "flex", gap: 4, padding: "0 20px", borderBottom: "1px solid var(--line-1)" }}>
          {[
            ["timeline", "Timeline & Logs"],
            ["artifacts", "Artifacts"],
            ["subagents", "Sub-agents"],
            ["metrics", "Metrics"],
            ["context", "Context"],
          ].map(([k, l]) => (
            <button key={k} onClick={() => setTab(k)} style={{
              padding: "10px 14px",
              fontSize: 12,
              color: tab === k ? "var(--fg-0)" : "var(--fg-3)",
              borderBottom: `2px solid ${tab === k ? color : "transparent"}`,
              marginBottom: -1,
              transition: "color 120ms",
            }}>{l}</button>
          ))}
        </div>

        {/* body */}
        <div style={{ flex: 1, overflow: "auto", padding: 24 }}>
          {tab === "timeline" && (
            <div style={{ display: "grid", gridTemplateColumns: "1.2fr 1fr", gap: 24, height: "100%" }}>
              <div>
                <div style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em", marginBottom: 10 }}>Live Stream</div>
                <div style={{ fontFamily: "var(--font-mono)", fontSize: 12, lineHeight: 1.6, background: "var(--bg-0)", padding: 14, borderRadius: 8, border: "1px solid var(--line-1)" }}>
                  {logs.map((l, i) => {
                    const lvlColor = l.lvl === "tool" ? color : l.lvl === "out" ? "var(--ok)" : l.lvl === "think" ? "var(--fg-3)" : "var(--fg-2)";
                    return (
                      <div key={i} style={{ display: "grid", gridTemplateColumns: "60px 52px 1fr", gap: 8, padding: "3px 0" }}>
                        <span style={{ color: "var(--fg-4)" }}>{l.t}</span>
                        <span style={{ color: lvlColor, fontSize: 10, textTransform: "uppercase" }}>{l.lvl}</span>
                        <span style={{ color: l.lvl === "think" ? "var(--fg-3)" : "var(--fg-1)", fontStyle: l.lvl === "think" ? "italic" : "normal" }}>{l.txt}</span>
                      </div>
                    );
                  })}
                  {agent.status === "working" && (
                    <div style={{ marginTop: 6, display: "flex", alignItems: "center", gap: 6, color: "var(--fg-3)" }}>
                      <span style={{ display: "inline-block", width: 6, height: 12, background: color, animation: "pulse 1s ease-in-out infinite" }} />
                      <span>thinking…</span>
                    </div>
                  )}
                </div>
              </div>
              <div>
                <div style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em", marginBottom: 10 }}>Step Plan</div>
                {[
                  ["✓", "Read existing auth module"],
                  ["✓", "Identify refresh flow gap"],
                  ["✓", "Implement middleware with context"],
                  ["●", "Write & run tests"],
                  ["○", "Open PR with description"],
                  ["○", "Request review from @ops"],
                ].map(([m, t], i) => (
                  <div key={i} style={{ display: "grid", gridTemplateColumns: "20px 1fr", gap: 10, padding: "7px 0", borderBottom: "1px solid var(--line-1)", fontSize: 13 }}>
                    <span style={{ color: m === "✓" ? "var(--ok)" : m === "●" ? color : "var(--fg-4)", fontFamily: "var(--font-mono)" }}>{m}</span>
                    <span style={{ color: m === "○" ? "var(--fg-3)" : "var(--fg-1)" }}>{t}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
          {tab === "artifacts" && (
            <div>
              <div style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em", marginBottom: 10 }}>Files touched</div>
              {artifacts.map((f, i) => (
                <div key={i} style={{ display: "grid", gridTemplateColumns: "80px 1fr auto", gap: 14, padding: "10px 0", borderBottom: "1px solid var(--line-1)", alignItems: "center" }}>
                  <span style={{
                    fontSize: 10, fontFamily: "var(--font-mono)", fontWeight: 600, textTransform: "uppercase",
                    color: f.status === "created" ? "var(--ok)" : f.status === "modified" ? "var(--warn)" : "var(--fg-3)",
                    padding: "3px 7px", borderRadius: 4,
                    background: f.status === "created" ? "var(--ok-bg)" : f.status === "modified" ? "var(--warn-bg)" : "var(--bg-3)",
                    textAlign: "center",
                  }}>{f.status}</span>
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 13, color: "var(--fg-0)" }}>{f.path}</span>
                  <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--fg-3)" }}>{f.lines}</span>
                </div>
              ))}
            </div>
          )}
          {tab === "subagents" && (
            <div>
              <div style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em", marginBottom: 10 }}>Spawned sub-agents</div>
              {subagents.map((s, i) => (
                <div key={i} style={{ display: "grid", gridTemplateColumns: "22px 1fr auto auto", gap: 14, padding: "10px 0", borderBottom: "1px solid var(--line-1)", alignItems: "center" }}>
                  <span className={`pip ${s.status === "working" ? "working" : "done"}`} />
                  <span style={{ fontSize: 13, color: "var(--fg-0)", fontFamily: "var(--font-mono)" }}>{s.name}</span>
                  <span style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "capitalize" }}>{s.status}</span>
                  <span style={{ fontSize: 11, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>{s.duration}</span>
                </div>
              ))}
            </div>
          )}
          {tab === "metrics" && (
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 20 }}>
              {[
                ["CPU usage",    cpuTrend,   "%",    color],
                ["Token rate",   tokenTrend, "tok/s", color],
                ["Cost over time",[0.2,0.4,0.7,1.1,1.4,1.8,2.2,2.6,2.9,3.1,3.3,3.41], "$", "var(--warn)"],
                ["Tool calls/min",[2,4,3,5,6,4,7,8,6,7,9,8], "calls", "var(--accent)"],
              ].map(([label, data, unit, c]) => (
                <div key={label} style={{ background: "var(--bg-2)", borderRadius: 10, padding: 16, border: "1px solid var(--line-1)" }}>
                  <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 10 }}>
                    <span style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em" }}>{label}</span>
                    <span style={{ fontSize: 14, color: "var(--fg-0)", fontFamily: "var(--font-mono)" }}>{data[data.length-1]}{unit === "%" ? "%" : ` ${unit}`}</span>
                  </div>
                  <Sparkline data={data} color={c} height={56} />
                </div>
              ))}
            </div>
          )}
          {tab === "context" && (
            <div style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--fg-2)", lineHeight: 1.7 }}>
              <div style={{ fontSize: 11, color: "var(--fg-3)", textTransform: "uppercase", letterSpacing: "0.06em", marginBottom: 10, fontFamily: "var(--font-sans)" }}>System prompt & context window</div>
              <div style={{ background: "var(--bg-0)", padding: 16, borderRadius: 8, border: "1px solid var(--line-1)" }}>
                <div style={{ color: "var(--fg-4)" }}># system</div>
                <div>You are {agent.name}, a {agent.type} agent in the Lord Helm fleet.</div>
                <div>You have access to: {agent.tools.join(", ")}.</div>
                <div>Report to orchestrator at /helm/v1/status every 30s.</div>
                <br />
                <div style={{ color: "var(--fg-4)" }}># current task</div>
                <div>{agent.task}</div>
                <br />
                <div style={{ color: "var(--fg-4)" }}># context usage</div>
                <div>{agent.tokens.toLocaleString()} / 200,000 tokens · {Math.round(agent.tokens / 2000)}% of window</div>
                <div style={{ height: 4, background: "var(--bg-3)", borderRadius: 999, marginTop: 6, overflow: "hidden" }}>
                  <div style={{ width: `${Math.min(100, agent.tokens / 2000)}%`, height: "100%", background: color }} />
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

window.AgentDeepDive = AgentDeepDive;
