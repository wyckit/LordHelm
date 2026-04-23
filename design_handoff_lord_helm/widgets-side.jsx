// Lord Helm — side widgets (alerts, approvals, recent, queue, health, chat)
const { useState: useStateS, useRef: useRefS, useEffect: useEffectS } = React;

// ---------- Alerts ----------
function Alerts() {
  const icons = { err: "⚠", warn: "!", info: "i", ok: "✓" };
  const colorMap = { err: "var(--err)", warn: "var(--warn)", info: "var(--info)", ok: "var(--ok)" };
  const bgMap = { err: "var(--err-bg)", warn: "var(--warn-bg)", info: "var(--info-bg)", ok: "var(--ok-bg)" };
  return (
    <div>
      {ALERTS.map((a, i) => (
        <div key={a.id} style={{ display: "grid", gridTemplateColumns: "22px 1fr auto", gap: 10, padding: "10px 14px", borderBottom: i < ALERTS.length - 1 ? "1px solid var(--line-1)" : "none", alignItems: "center" }}>
          <div style={{ width: 22, height: 22, borderRadius: 5, background: bgMap[a.level], color: colorMap[a.level], display: "grid", placeItems: "center", fontSize: 11, fontWeight: 700 }}>
            {icons[a.level]}
          </div>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 12.5, color: "var(--fg-0)", marginBottom: 2, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{a.title}</div>
            <div style={{ fontSize: 11, color: "var(--fg-3)", fontFamily: "var(--font-mono)" }}>{a.meta}</div>
          </div>
          <div style={{ fontSize: 11, color: "var(--fg-4)", fontVariantNumeric: "tabular-nums" }}>{a.ago}</div>
        </div>
      ))}
    </div>
  );
}

// ---------- Approvals ----------
function Approvals() {
  const riskColor = { high: "var(--err)", med: "var(--warn)", low: "var(--ok)" };
  return (
    <div>
      {APPROVALS.map((a, i) => (
        <div key={a.id} style={{ padding: "10px 14px", borderBottom: i < APPROVALS.length - 1 ? "1px solid var(--line-1)" : "none" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 6, marginBottom: 4 }}>
            <span style={{ fontSize: 11, color: "var(--fg-3)" }}>{a.agent}</span>
            <span style={{ fontSize: 9, color: riskColor[a.risk], textTransform: "uppercase", letterSpacing: "0.05em", padding: "1px 5px", borderRadius: 3, background: `color-mix(in oklab, ${riskColor[a.risk]} 15%, transparent)` }}>{a.risk} risk</span>
          </div>
          <div style={{ fontSize: 12.5, color: "var(--fg-0)", marginBottom: 8, lineHeight: 1.4 }}>{a.action}</div>
          <div style={{ display: "flex", gap: 6 }}>
            <button style={{ flex: 1, padding: "5px 10px", background: "var(--brand-2)", color: "white", fontSize: 11, fontWeight: 500, borderRadius: 5, transition: "background 120ms" }}
              onMouseEnter={e => e.currentTarget.style.background = "var(--brand)"}
              onMouseLeave={e => e.currentTarget.style.background = "var(--brand-2)"}
            >Approve</button>
            <button style={{ flex: 1, padding: "5px 10px", background: "var(--bg-3)", color: "var(--fg-1)", fontSize: 11, borderRadius: 5, border: "1px solid var(--line-2)" }}>Deny</button>
          </div>
        </div>
      ))}
    </div>
  );
}

// ---------- Recent completions ----------
function Recent() {
  const kindIcons = { pr: "⌥", artifact: "◈", doc: "▤", report: "▦" };
  return (
    <div>
      {RECENT.map((r, i) => (
        <div key={r.id} style={{ display: "grid", gridTemplateColumns: "22px 1fr auto", gap: 10, padding: "9px 14px", borderBottom: i < RECENT.length - 1 ? "1px solid var(--line-1)" : "none", alignItems: "center" }}>
          <div style={{ width: 22, height: 22, borderRadius: 5, background: "var(--bg-3)", color: "var(--fg-2)", display: "grid", placeItems: "center", fontSize: 12 }}>
            {kindIcons[r.kind]}
          </div>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 12.5, color: "var(--fg-0)", fontFamily: "var(--font-mono)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{r.title}</div>
            <div style={{ fontSize: 11, color: "var(--fg-3)" }}>{r.agent}</div>
          </div>
          <div style={{ fontSize: 11, color: "var(--fg-4)", fontVariantNumeric: "tabular-nums" }}>{r.time}</div>
        </div>
      ))}
    </div>
  );
}

// ---------- Queue ----------
function Queue() {
  const pColor = { p1: "var(--err)", p2: "var(--warn)", p3: "var(--fg-3)" };
  return (
    <div>
      {QUEUE.map((q, i) => (
        <div key={q.id} style={{ display: "grid", gridTemplateColumns: "auto 1fr auto", gap: 10, padding: "10px 14px", borderBottom: i < QUEUE.length - 1 ? "1px solid var(--line-1)" : "none", alignItems: "center" }}>
          <span style={{ fontSize: 10, color: pColor[q.priority], fontFamily: "var(--font-mono)", fontWeight: 700, textTransform: "uppercase" }}>{q.priority}</span>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 12.5, color: "var(--fg-0)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{q.title}</div>
            <div style={{ fontSize: 11, color: "var(--fg-3)" }}>→ {q.agent} · est {q.eta}</div>
          </div>
          <button style={{ fontSize: 11, color: "var(--fg-3)", padding: "3px 8px", borderRadius: 4, border: "1px solid var(--line-2)" }}>Assign</button>
        </div>
      ))}
    </div>
  );
}

// ---------- System health ----------
function Health() {
  return (
    <div style={{ padding: "12px 14px", display: "grid", gridTemplateColumns: "1fr 1fr", gap: 14 }}>
      {HEALTH.map(h => {
        const pct = (h.value / h.cap) * 100;
        const color = pct > 80 ? "var(--err)" : pct > 60 ? "var(--warn)" : "var(--ok)";
        return (
          <div key={h.label}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", marginBottom: 6 }}>
              <span style={{ fontSize: 11, color: "var(--fg-2)" }}>{h.label}</span>
              <span style={{ fontSize: 12, color: "var(--fg-0)", fontFamily: "var(--font-mono)", fontVariantNumeric: "tabular-nums" }}>
                {h.value}{h.unit === "%" ? "%" : ` ${h.unit}`}
              </span>
            </div>
            <div style={{ height: 4, background: "var(--bg-3)", borderRadius: 999, overflow: "hidden" }}>
              <div style={{ width: `${pct}%`, height: "100%", background: color, transition: "width 400ms" }} />
            </div>
            {h.note && <div style={{ fontSize: 10, color: "var(--fg-4)", marginTop: 3, fontFamily: "var(--font-mono)" }}>{h.note}</div>}
          </div>
        );
      })}
    </div>
  );
}

// ---------- Chat with Lord Helm ----------
function HelmChat({ compact = false }) {
  const [messages, setMessages] = useStateS([
    { role: "helm", text: "Fleet status: 7 working, 2 waiting, 2 blocked. Sentinel needs prod DB approval — shall I escalate?", time: "just now" },
  ]);
  const [input, setInput] = useStateS("");
  const [typing, setTyping] = useStateS(false);
  const scrollRef = useRefS(null);

  useEffectS(() => {
    scrollRef.current?.scrollTo({ top: 9e9, behavior: "smooth" });
  }, [messages, typing]);

  const suggestions = [
    "What's blocking Compass?",
    "Show me today's spend",
    "Rebalance the research pod",
  ];

  const send = (txt) => {
    const t = (txt ?? input).trim();
    if (!t) return;
    setMessages(m => [...m, { role: "me", text: t, time: "now" }]);
    setInput("");
    setTyping(true);
    setTimeout(() => {
      setTyping(false);
      setMessages(m => [...m, {
        role: "helm",
        text: "Compass is blocked on an expired Crunchbase v4 API key. I've paused the agent and filed an ops ticket. Want me to rotate the key via 1Password?",
        time: "now"
      }]);
    }, 1600);
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <div ref={scrollRef} style={{ flex: 1, overflow: "auto", padding: "14px" }}>
        {messages.map((m, i) => (
          <div key={i} style={{
            display: "flex",
            justifyContent: m.role === "me" ? "flex-end" : "flex-start",
            marginBottom: 10,
            animation: "fadeUp 300ms ease both",
          }}>
            {m.role === "helm" && (
              <div style={{ width: 24, height: 24, borderRadius: 6, background: "linear-gradient(135deg, var(--brand-2), var(--brand))", color: "white", display: "grid", placeItems: "center", fontSize: 12, marginRight: 8, flexShrink: 0 }}>♛</div>
            )}
            <div style={{
              maxWidth: "82%",
              padding: "8px 12px",
              background: m.role === "me" ? "var(--brand-2)" : "var(--bg-3)",
              color: m.role === "me" ? "white" : "var(--fg-0)",
              borderRadius: m.role === "me" ? "12px 12px 2px 12px" : "12px 12px 12px 2px",
              fontSize: 12.5,
              lineHeight: 1.5,
              border: m.role === "helm" ? "1px solid var(--line-2)" : "none",
            }}>
              {m.text}
            </div>
          </div>
        ))}
        {typing && (
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <div style={{ width: 24, height: 24, borderRadius: 6, background: "linear-gradient(135deg, var(--brand-2), var(--brand))", color: "white", display: "grid", placeItems: "center", fontSize: 12 }}>♛</div>
            <div style={{ display: "flex", gap: 3, padding: "8px 12px", background: "var(--bg-3)", borderRadius: "12px 12px 12px 2px", border: "1px solid var(--line-2)" }}>
              {[0,1,2].map(i => (
                <span key={i} style={{ width: 5, height: 5, borderRadius: 999, background: "var(--fg-3)", animation: `pulse 1.2s ease-in-out infinite`, animationDelay: `${i*150}ms` }} />
              ))}
            </div>
          </div>
        )}
      </div>
      {!compact && (
        <div style={{ padding: "0 14px 8px", display: "flex", gap: 6, flexWrap: "wrap" }}>
          {suggestions.map(s => (
            <button key={s} onClick={() => send(s)} style={{
              fontSize: 11, padding: "4px 9px", borderRadius: 999,
              background: "var(--bg-3)", color: "var(--fg-2)",
              border: "1px solid var(--line-2)", whiteSpace: "nowrap",
            }}>{s}</button>
          ))}
        </div>
      )}
      <form onSubmit={e => { e.preventDefault(); send(); }} style={{ padding: "10px 14px 14px", borderTop: "1px solid var(--line-1)" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "8px 10px", background: "var(--bg-0)", borderRadius: 8, border: "1px solid var(--line-2)" }}>
          <span style={{ color: "var(--brand)", fontSize: 13 }}>♛</span>
          <input
            value={input}
            onChange={e => setInput(e.target.value)}
            placeholder="Ask Lord Helm…"
            style={{ flex: 1, background: "transparent", border: "none", outline: "none", color: "var(--fg-0)", fontSize: 13 }}
          />
          <span style={{ fontSize: 10, color: "var(--fg-4)", fontFamily: "var(--font-mono)" }}>⏎</span>
        </div>
      </form>
    </div>
  );
}

Object.assign(window, { Alerts, Approvals, Recent, Queue, Health, HelmChat });
