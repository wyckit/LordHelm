// Shared mock data for Lord Helm
const AGENTS = [
  { id: "a01", name: "Scribe-7",     type: "write",    status: "working", task: "Drafting Q2 investor memo — section 3 of 5", progress: 62, runtime: "00:14:22", tokens: 84_210, cost: 1.82, model: "claude-opus-4", tools: ["web", "docs"] },
  { id: "a02", name: "Cartographer", type: "research", status: "working", task: "Mapping competitor pricing across 14 SaaS vendors", progress: 41, runtime: "00:08:47", tokens: 52_104, cost: 0.94, model: "claude-sonnet-4", tools: ["web", "browser"] },
  { id: "a03", name: "Forgemaster",  type: "code",     status: "working", task: "Implementing auth middleware · PR #4821", progress: 78, runtime: "00:22:11", tokens: 128_900, cost: 3.41, model: "claude-opus-4", tools: ["code", "bash", "git"] },
  { id: "a04", name: "Herald",       type: "ops",      status: "waiting", task: "Queued: deploy staging → prod cutover", progress: 0,  runtime: "00:00:00", tokens: 0, cost: 0, model: "claude-haiku-4", tools: ["k8s", "bash"] },
  { id: "a05", name: "Oracle",       type: "data",     status: "working", task: "Weekly retention cohort analysis", progress: 88, runtime: "00:31:05", tokens: 201_442, cost: 4.12, model: "claude-opus-4", tools: ["sql", "python"] },
  { id: "a06", name: "Sentinel",     type: "ops",      status: "blocked", task: "Waiting on approval: prod DB migration", progress: 34, runtime: "01:02:18", tokens: 18_220, cost: 0.31, model: "claude-sonnet-4", tools: ["sql", "k8s"] },
  { id: "a07", name: "Loom",         type: "design",   status: "working", task: "Generating 6 hero variants for landing v3", progress: 55, runtime: "00:11:03", tokens: 44_180, cost: 1.22, model: "claude-sonnet-4", tools: ["figma", "image"] },
  { id: "a08", name: "Vellum",       type: "write",    status: "idle",    task: "Standby", progress: 0, runtime: "00:00:00", tokens: 0, cost: 0, model: "claude-haiku-4", tools: ["docs"] },
  { id: "a09", name: "Ledger",       type: "data",     status: "working", task: "Reconciling billing events · 2.3M rows", progress: 19, runtime: "00:04:52", tokens: 31_400, cost: 0.58, model: "claude-sonnet-4", tools: ["sql"] },
  { id: "a10", name: "Beacon",       type: "research", status: "waiting", task: "Queued: regulatory scan — GDPR/CCPA deltas", progress: 0, runtime: "00:00:00", tokens: 0, cost: 0, model: "claude-haiku-4", tools: ["web"] },
  { id: "a11", name: "Anvil",        type: "code",     status: "working", task: "Refactoring payment module · 9 files touched", progress: 71, runtime: "00:18:33", tokens: 96_220, cost: 2.44, model: "claude-opus-4", tools: ["code", "git"] },
  { id: "a12", name: "Compass",      type: "research", status: "blocked", task: "Needs API key: Crunchbase v4", progress: 12, runtime: "00:02:14", tokens: 4_100, cost: 0.08, model: "claude-haiku-4", tools: ["web"] },
];

const AGENT_COLORS = {
  code: "var(--hue-code)",
  research: "var(--hue-research)",
  data: "var(--hue-data)",
  ops: "var(--hue-ops)",
  write: "var(--hue-write)",
  design: "var(--hue-design)",
};

const AGENT_GLYPHS = {
  code: "‹/›",
  research: "◎",
  data: "▦",
  ops: "⚙",
  write: "✎",
  design: "◈",
};

const ALERTS = [
  { id: "al1", level: "err",  title: "Sentinel blocked · awaiting approval",  meta: "prod DB migration", ago: "2m" },
  { id: "al2", level: "warn", title: "Claude API p95 latency elevated",        meta: "2.8s → 4.1s",        ago: "7m" },
  { id: "al3", level: "warn", title: "Compass: Crunchbase key expired",        meta: "agent blocked",      ago: "14m" },
  { id: "al4", level: "info", title: "Forgemaster opened PR #4821",            meta: "auth/middleware",    ago: "22m" },
  { id: "al5", level: "ok",   title: "Loom completed 6 hero variants",         meta: "landing v3",         ago: "38m" },
  { id: "al6", level: "info", title: "Cost budget 62% consumed (daily)",       meta: "$248.40 / $400",     ago: "1h" },
];

const APPROVALS = [
  { id: "ap1", agent: "Sentinel",   action: "Migrate users table · add index idx_email_lower",    risk: "high" },
  { id: "ap2", agent: "Herald",     action: "Deploy v2.18.3 to production",                        risk: "med" },
  { id: "ap3", agent: "Forgemaster",action: "Merge PR #4821 · auth middleware",                    risk: "low" },
  { id: "ap4", agent: "Oracle",     action: "Send retention report to exec@",                      risk: "low" },
];

const RECENT = [
  { id: "r1", agent: "Loom",         kind: "artifact", title: "hero-v3-variants.fig",   time: "2m" },
  { id: "r2", agent: "Forgemaster",  kind: "pr",       title: "PR #4821 · auth middleware", time: "22m" },
  { id: "r3", agent: "Scribe-7",     kind: "doc",      title: "Q1 board notes · final",   time: "41m" },
  { id: "r4", agent: "Oracle",       kind: "report",   title: "cohort_2026_04.csv",       time: "1h" },
  { id: "r5", agent: "Anvil",        kind: "pr",       title: "PR #4816 · refactor/payments", time: "2h" },
  { id: "r6", agent: "Cartographer",kind: "doc",       title: "competitor-pricing-apr.md", time: "3h" },
];

const QUEUE = [
  { id: "q1", title: "Refresh weekly KPI deck",        priority: "p1", eta: "8m",  agent: "unassigned" },
  { id: "q2", title: "Triage 23 new GitHub issues",    priority: "p2", eta: "15m", agent: "unassigned" },
  { id: "q3", title: "Generate blog post · roadmap",   priority: "p3", eta: "30m", agent: "Vellum" },
  { id: "q4", title: "Scan for GDPR policy changes",   priority: "p2", eta: "20m", agent: "Beacon" },
  { id: "q5", title: "Backfill analytics events",      priority: "p3", eta: "1h",  agent: "unassigned" },
];

const KPIS = [
  { label: "Tasks / hr",   value: "47.2", delta: "+12%", trend: [18, 22, 19, 28, 31, 27, 34, 41, 38, 42, 45, 47] },
  { label: "Success rate", value: "94.8%", delta: "+0.4%", trend: [92, 93, 91, 94, 93, 95, 94, 95, 94, 95, 95, 95] },
  { label: "Tokens / min", value: "21.4k", delta: "+8%",  trend: [12, 14, 11, 16, 18, 15, 17, 19, 20, 18, 21, 21] },
  { label: "Spend / day",  value: "$248",  delta: "-3%",  trend: [280, 270, 265, 260, 255, 260, 250, 245, 248, 252, 250, 248] },
];

const HEALTH = [
  { label: "CPU",         value: 42, unit: "%",    cap: 100 },
  { label: "Memory",      value: 68, unit: "%",    cap: 100 },
  { label: "API · Claude",value: 64, unit: "%",    cap: 100, note: "64k/100k RPM" },
  { label: "API · OpenAI",value: 12, unit: "%",    cap: 100, note: "6k/50k RPM" },
  { label: "Vector DB",   value: 31, unit: "%",    cap: 100 },
  { label: "Queue depth", value: 7,  unit: "jobs", cap: 50  },
];

// Topology: nodes + edges
const TOPOLOGY_NODES = [
  { id: "helm",    label: "Lord Helm", kind: "helm",    x: 50, y: 50, size: 36 },
  { id: "orc-w",   label: "Write Pod",    kind: "orc",     x: 22, y: 28, size: 22 },
  { id: "orc-c",   label: "Code Pod",     kind: "orc",     x: 78, y: 28, size: 22 },
  { id: "orc-d",   label: "Data Pod",     kind: "orc",     x: 22, y: 72, size: 22 },
  { id: "orc-o",   label: "Ops Pod",      kind: "orc",     x: 78, y: 72, size: 22 },
  { id: "a01",     label: "Scribe-7",     kind: "agent",   agentType: "write",    status: "working", x: 12, y: 14, size: 14 },
  { id: "a08",     label: "Vellum",       kind: "agent",   agentType: "write",    status: "idle",    x: 30, y: 12, size: 14 },
  { id: "a03",     label: "Forgemaster",  kind: "agent",   agentType: "code",     status: "working", x: 88, y: 14, size: 14 },
  { id: "a11",     label: "Anvil",        kind: "agent",   agentType: "code",     status: "working", x: 70, y: 12, size: 14 },
  { id: "a02",     label: "Cartographer", kind: "agent",   agentType: "research", status: "working", x: 88, y: 42, size: 14 },
  { id: "a10",     label: "Beacon",       kind: "agent",   agentType: "research", status: "waiting", x: 92, y: 60, size: 14 },
  { id: "a12",     label: "Compass",      kind: "agent",   agentType: "research", status: "blocked", x: 60, y: 42, size: 14 },
  { id: "a05",     label: "Oracle",       kind: "agent",   agentType: "data",     status: "working", x: 8,  y: 62, size: 14 },
  { id: "a09",     label: "Ledger",       kind: "agent",   agentType: "data",     status: "working", x: 12, y: 84, size: 14 },
  { id: "a04",     label: "Herald",       kind: "agent",   agentType: "ops",      status: "waiting", x: 88, y: 86, size: 14 },
  { id: "a06",     label: "Sentinel",     kind: "agent",   agentType: "ops",      status: "blocked", x: 68, y: 84, size: 14 },
  { id: "a07",     label: "Loom",         kind: "agent",   agentType: "design",   status: "working", x: 42, y: 18, size: 14 },
];
const TOPOLOGY_EDGES = [
  ["helm", "orc-w"], ["helm", "orc-c"], ["helm", "orc-d"], ["helm", "orc-o"],
  ["orc-w", "a01"], ["orc-w", "a08"], ["orc-w", "a07"],
  ["orc-c", "a03"], ["orc-c", "a11"],
  ["orc-c", "a02"], ["orc-c", "a10"], ["orc-c", "a12"], // research under code pod (mixed)
  ["orc-d", "a05"], ["orc-d", "a09"],
  ["orc-o", "a04"], ["orc-o", "a06"],
];

// Live agent logs (for active agent detail)
const AGENT_LOGS = {
  a03: [
    { t: "00:22:09", lvl: "tool",  txt: "bash · `pytest tests/auth/ -k middleware`" },
    { t: "00:22:06", lvl: "out",   txt: "✓ 14 passed · 0 failed · 2.4s" },
    { t: "00:21:58", lvl: "think", txt: "All middleware tests green. Moving to PR description." },
    { t: "00:21:44", lvl: "tool",  txt: "edit · `src/auth/middleware.ts` (+42 −18)" },
    { t: "00:21:30", lvl: "tool",  txt: "read · `src/auth/session.ts`" },
    { t: "00:21:12", lvl: "think", txt: "Need to thread the user context through the refresh flow." },
    { t: "00:20:58", lvl: "tool",  txt: "git · `git diff HEAD~3 -- src/auth/`" },
  ],
};

Object.assign(window, {
  AGENTS, AGENT_COLORS, AGENT_GLYPHS,
  ALERTS, APPROVALS, RECENT, QUEUE, KPIS, HEALTH,
  TOPOLOGY_NODES, TOPOLOGY_EDGES, AGENT_LOGS,
});
