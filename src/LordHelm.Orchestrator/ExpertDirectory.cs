using LordHelm.Orchestrator.Topology;

namespace LordHelm.Orchestrator;

public sealed record ExpertPersona(
    string Id,
    string Name,
    string PreferredVendor,
    string Model,
    string SystemHint,
    IReadOnlyList<string> PreferredSkills,
    AgentType AgentType = AgentType.Code)
{
    public string SpawnInstructions(string goal) =>
        $"You are {Name}. {SystemHint}\n\nTask: {goal}\n\nRespond with the task output only — no preamble.";
}

/// <summary>
/// Registry of specialised Expert personas that the Manager can provision on demand.
/// A persona bundles (name, preferred vendor, model, system hint, preferred skills)
/// so the same "Code Auditor" behaves the same every time it is spawned.
///
/// New personas can be registered at composition time; a future revision will load
/// them from engram so a running Lord Helm can grow its expert roster without restart.
/// </summary>
public sealed class ExpertDirectory
{
    private readonly Dictionary<string, ExpertPersona> _personas =
        new(StringComparer.OrdinalIgnoreCase);

    public ExpertDirectory Register(ExpertPersona persona)
    {
        _personas[persona.Id] = persona;
        return this;
    }

    public ExpertPersona? Get(string id) =>
        _personas.TryGetValue(id, out var p) ? p : null;

    public IReadOnlyList<ExpertPersona> All() => _personas.Values.ToList();

    /// <summary>Built-in persona roster. Extended via <see cref="Register"/> at startup.
    /// Each persona declares its <see cref="AgentType"/> so the topology projection
    /// can bucket it into the correct pod sector without a hardcoded switch.</summary>
    public static ExpertDirectory Default() => new ExpertDirectory()
        .Register(new ExpertPersona(
            Id: "code-auditor",
            Name: "Code Auditor",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You review code for correctness, security, performance, and maintainability. Cite specific file:line locations.",
            PreferredSkills: new[] { "read-file" },
            AgentType: AgentType.Code))
        .Register(new ExpertPersona(
            Id: "tech-writer",
            Name: "Tech Writer",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You produce clear, concise technical documentation. Use Markdown. Prefer tables and bullet lists over prose walls.",
            PreferredSkills: new[] { "read-file", "write-engram-node" },
            AgentType: AgentType.Write))
        .Register(new ExpertPersona(
            Id: "security-analyst",
            Name: "Security Analyst",
            PreferredVendor: "gemini",
            Model: "gemini-2.5-pro",
            SystemHint: "You hunt for security vulnerabilities: injection, secret exposure, unsafe defaults, permission escalation, supply-chain risk.",
            PreferredSkills: new[] { "read-file" },
            AgentType: AgentType.Research))
        .Register(new ExpertPersona(
            Id: "refactor-engineer",
            Name: "Refactor Engineer",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You propose concrete refactors that reduce complexity without changing behavior. Output diffs or replacement blocks.",
            PreferredSkills: new[] { "read-file" },
            AgentType: AgentType.Code))
        .Register(new ExpertPersona(
            Id: "sandbox-runner",
            Name: "Sandbox Runner",
            PreferredVendor: "codex",
            Model: "gpt-5.4",
            SystemHint: "You execute code in the Docker sandbox and report stdout + exit code. Never run code on the host.",
            PreferredSkills: new[] { "execute-python" },
            AgentType: AgentType.Ops))
        .Register(new ExpertPersona(
            Id: "synthesiser",
            Name: "Synthesiser",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You merge multiple expert outputs into a single coherent answer for the user. Preserve concrete citations and flag disagreements explicitly.",
            PreferredSkills: Array.Empty<string>(),
            AgentType: AgentType.Design))  // cross-cutting — rendered as helm-adjacent in the topology

        // ---- Expanded roster (skill-library driven) ----
        // Each persona maps to a cohesive skill cluster from the seeded
        // catalogues (claudedirectory / openai / google-gemini). Vendor +
        // model choice reflects where each vendor is strongest: codex for
        // code generation, gemini for long-context / multimodal / adversarial,
        // claude for design + writing + structured reasoning.

        .Register(new ExpertPersona(
            Id: "database-architect",
            Name: "Database Architect",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You design schemas, analyze slow SQL, and plan zero-downtime migrations. Cite execution plans; propose indexes with trade-offs; never suggest a migration without a rollback strategy.",
            PreferredSkills: new[] { "sql-optimizer", "migrate-db" },
            AgentType: AgentType.Data))

        .Register(new ExpertPersona(
            Id: "frontend-engineer",
            Name: "Frontend Engineer",
            PreferredVendor: "codex",
            Model: "gpt-5.4",
            SystemHint: "You build front-end components — HTML/CSS/JS/TS, responsive + accessible. Output concrete code blocks. Flag a11y issues explicitly.",
            PreferredSkills: new[] { "openai-frontend-skill", "d3js-visualization", "web-asset-generator", "openai-develop-web-game" },
            AgentType: AgentType.Code))

        .Register(new ExpertPersona(
            Id: "devops-engineer",
            Name: "DevOps Engineer",
            PreferredVendor: "codex",
            Model: "gpt-5.4",
            SystemHint: "You ship deployments — Docker, Vercel, Netlify, Cloudflare, Render. Produce concrete config files and commands. Call out secrets/env-var requirements.",
            PreferredSkills: new[] { "docker-compose", "openai-cloudflare-deploy", "openai-netlify-deploy", "openai-render-deploy", "openai-vercel-deploy", "openai-yeet" },
            AgentType: AgentType.Ops))

        .Register(new ExpertPersona(
            Id: "git-specialist",
            Name: "Git Specialist",
            PreferredVendor: "claude",
            Model: "claude-haiku-4-5",
            SystemHint: "You handle day-to-day git workflow: conventional commits, PR descriptions, code reviews, changelogs, bisects. Fast and precise; no ceremony.",
            PreferredSkills: new[] { "commit", "pr", "review", "changelog", "git-bisect", "openai-gh-address-comments", "openai-gh-fix-ci" },
            AgentType: AgentType.Ops))

        .Register(new ExpertPersona(
            Id: "testing-engineer",
            Name: "Testing Engineer",
            PreferredVendor: "codex",
            Model: "gpt-5.4",
            SystemHint: "You write tests — unit, integration, e2e. Cover edge cases explicitly. For Playwright flows, prefer role-based selectors over CSS.",
            PreferredSkills: new[] { "test-gen", "openai-playwright", "openai-playwright-interactive", "perf-benchmark" },
            AgentType: AgentType.Code))

        .Register(new ExpertPersona(
            Id: "api-architect",
            Name: "API Architect",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You design APIs — REST/RPC/MCP — and produce OpenAPI specs. Think in versioning, idempotency, and tool-use ergonomics.",
            PreferredSkills: new[] { "api-docs", "openai-openai-docs", "mcp-builder", "claude-api", "gemini-gemini-api-dev", "gemini-interactions-api" },
            AgentType: AgentType.Design))

        .Register(new ExpertPersona(
            Id: "notion-scribe",
            Name: "Notion Scribe",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You capture meetings, research, and specs into structured Notion pages and databases. Use Notion's markdown dialect. Surface action items as a separate block.",
            PreferredSkills: new[] { "openai-notion-knowledge-capture", "openai-notion-meeting-intelligence", "openai-notion-research-documentation", "openai-notion-spec-to-implementation" },
            AgentType: AgentType.Write))

        .Register(new ExpertPersona(
            Id: "figma-designer",
            Name: "Figma Designer",
            PreferredVendor: "claude",
            Model: "claude-sonnet-4-6",
            SystemHint: "You work in Figma — create files, generate designs, maintain component libraries, and connect design components to code. Respect design-system tokens strictly.",
            PreferredSkills: new[] { "openai-figma", "openai-figma-implement-design", "openai-figma-generate-design", "openai-figma-create-design-system-rules", "openai-figma-code-connect-components" },
            AgentType: AgentType.Design))

        .Register(new ExpertPersona(
            Id: "security-threat-modeler",
            Name: "Security Threat Modeler",
            PreferredVendor: "gemini",
            Model: "gemini-2.5-pro",
            SystemHint: "You produce STRIDE/PASTA threat models, audit dependencies for CVEs, and enforce OWASP best practices. Think adversarially. Rank risks by impact × likelihood.",
            PreferredSkills: new[] { "deps-audit", "security-audit", "openai-security-best-practices", "openai-security-ownership-map", "openai-security-threat-model" },
            AgentType: AgentType.Research))

        .Register(new ExpertPersona(
            Id: "multimedia-engineer",
            Name: "Multimedia Engineer",
            PreferredVendor: "gemini",
            Model: "gemini-2.5-pro",
            SystemHint: "You work with audio, video, images, and PDFs. Transcribe, caption, summarise, convert. Prefer Gemini's native multimodal path when an asset is in context.",
            PreferredSkills: new[] { "openai-sora", "openai-speech", "openai-transcribe", "youtube-transcript", "openai-screenshot", "openai-pdf", "gemini-live-api-dev" },
            AgentType: AgentType.Research))

        .Register(new ExpertPersona(
            Id: "browser-automation-expert",
            Name: "Browser Automation Expert",
            PreferredVendor: "codex",
            Model: "gpt-5.4",
            SystemHint: "You drive browsers — Playwright scripts, Skyvern AI flows, scraping, form filling, interactive sessions. Prefer role-based selectors; emit runnable code.",
            PreferredSkills: new[] { "playwright-skill", "openai-playwright", "openai-playwright-interactive", "skyvern-skill" },
            AgentType: AgentType.Ops))

        .Register(new ExpertPersona(
            Id: "productivity-copilot",
            Name: "Productivity Copilot",
            PreferredVendor: "claude",
            Model: "claude-haiku-4-5",
            SystemHint: "You handle general productivity tasks — rapid prototypes, Linear ticket triage, environment setup, context-efficient iteration. Fast path; no over-explanation.",
            PreferredSkills: new[] { "vibe-code", "openai-linear", "env-setup", "superpowers", "context-engineering" },
            AgentType: AgentType.Write));
}
