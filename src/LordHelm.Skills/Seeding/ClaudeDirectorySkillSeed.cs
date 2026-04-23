using System.Text;

namespace LordHelm.Skills.Seeding;

/// <summary>
/// One-time seeder that drops the <a href="https://www.claudedirectory.org/skills">
/// claudedirectory.org/skills</a> catalog into the skills/ directory as
/// <c>.skill.xml</c> manifests so a fresh install gets a real library
/// on first boot. Operators edit or delete entries afterwards; the seeder
/// never overwrites an existing file, so local modifications survive.
///
/// All seeded entries default to Host / Read / Low-trust — they're
/// reasoning-style "how-to" skills, not sandboxed executors. Re-classify
/// on a per-skill basis via the /skills admin page (future slice) when
/// the skill actually needs Docker / EXEC.
/// </summary>
public static class ClaudeDirectorySkillSeed
{
    public sealed record Seed(string Id, string Name, string Description, string[] Tags);

    public static IReadOnlyList<Seed> All { get; } = new[]
    {
        new Seed("sql-optimizer",        "SQL Query Optimizer",         "Analyze slow SQL queries, explain the execution plan, and suggest indexes or rewrites to make them fast", new[] { "sql", "database", "performance", "query-optimization" }),
        new Seed("commit",               "Git Commit",                  "Generate conventional commit messages and create commits with best practices", new[] { "git", "commit", "workflow" }),
        new Seed("pr",                   "Create Pull Request",         "Generate comprehensive pull request descriptions and create PRs via GitHub CLI", new[] { "git", "github", "pr", "workflow" }),
        new Seed("review",               "Code Review",                 "Perform thorough code reviews with actionable feedback", new[] { "review", "code-quality", "workflow" }),
        new Seed("superpowers",          "Superpowers",                 "Core software engineering competencies covering planning, reviewing, testing, and debugging", new[] { "sdlc", "planning", "testing", "debugging" }),
        new Seed("context-engineering",  "Context Engineering Kit",     "Advanced context engineering techniques and patterns with minimal token footprint for efficient Claude Code sessions", new[] { "context", "optimization", "efficiency", "tokens" }),
        new Seed("web-asset-generator",  "Web Assets Generator",        "Generate favicons, PWA app icons, and social media meta images with proper HTML tags", new[] { "favicon", "pwa", "meta-tags", "images" }),
        new Seed("playwright-skill",     "Playwright Browser Automation","Browser automation and testing using Playwright for web application testing and scraping", new[] { "playwright", "browser", "testing", "automation" }),
        new Seed("mcp-builder",          "MCP Server Builder",          "Guide for creating high-quality Model Context Protocol servers with best practices", new[] { "mcp", "server", "development", "protocol" }),
        new Seed("ios-simulator",        "iOS Simulator",               "Build, navigate, and test iOS apps via simulator automation with XcodeBuild integration", new[] { "ios", "xcode", "mobile", "testing" }),
        new Seed("youtube-transcript",   "YouTube Transcript Downloader","Download transcripts and captions from YouTube videos for analysis and summarization", new[] { "youtube", "transcript", "captions", "video" }),
        new Seed("d3js-visualization",   "D3.js Data Visualization",    "Create interactive data visualizations using D3.js with best practices for charts and graphs", new[] { "d3", "visualization", "charts", "data" }),
        new Seed("architecture-diagram", "Architecture Diagram Generator","Generate Mermaid diagrams showing system architecture, data flows, and component relationships", new[] { "architecture", "diagrams", "mermaid", "visualization" }),
        new Seed("api-docs",             "API Documentation Generator", "Analyze API endpoints in your codebase and generate OpenAPI/Swagger documentation", new[] { "api", "openapi", "swagger", "documentation" }),
        new Seed("migrate-db",           "Database Migration Planner",  "Plan and generate safe database migrations with rollback strategies and zero-downtime deployment", new[] { "database", "migrations", "sql", "schema" }),
        new Seed("claude-api",           "Claude API Builder",          "Build applications with the Claude API and Anthropic SDK with best practices for tool use", new[] { "api", "anthropic", "sdk", "claude" }),
        new Seed("refactor",             "Code Refactor",               "Systematically refactor code for improved readability, maintainability, and performance", new[] { "refactoring", "code-quality", "cleanup", "patterns" }),
        new Seed("test-gen",             "Test Generator",              "Generate comprehensive test suites with unit, integration, and edge case coverage", new[] { "testing", "test-generation", "coverage", "tdd" }),
        new Seed("changelog",            "Changelog Generator",         "Generate structured changelogs from git history following Keep a Changelog format", new[] { "changelog", "git", "release", "documentation" }),
        new Seed("deps-audit",           "Dependency Audit",            "Audit project dependencies for security vulnerabilities, outdated packages, and license compliance", new[] { "dependencies", "security", "audit", "npm" }),
        new Seed("security-audit",       "Security Audit",              "Run a comprehensive security audit covering OWASP Top 10, dependency vulnerabilities, secrets detection", new[] { "security", "audit", "owasp", "vulnerabilities" }),
        new Seed("docker-compose",       "Docker Compose Generator",    "Generate and optimize Docker Compose configurations for multi-service applications with networking", new[] { "docker", "containers", "compose", "devops" }),
        new Seed("perf-benchmark",       "Performance Benchmark",       "Profile and benchmark code performance, identify bottlenecks, and suggest optimizations", new[] { "performance", "benchmarking", "profiling", "optimization" }),
        new Seed("git-bisect",           "Git Bisect Helper",           "Automated git bisect to find the exact commit that introduced a bug", new[] { "git", "debugging", "bisect", "regression" }),
        new Seed("vibe-code",            "Vibe Code",                   "A slash command that helps developers rapidly prototype and build applications through natural language", new[] { "vibe-coding", "prototyping", "rapid-development", "productivity" }),
        new Seed("monorepo-manager",     "Monorepo Manager",            "Manage monorepo operations including dependency graphs, affected package detection, build ordering", new[] { "monorepo", "workspace", "dependencies", "build" }),
        new Seed("regex-builder",        "Regex Builder",               "Interactively build, explain, and test regular expressions with examples and edge case validation", new[] { "regex", "utility", "validation", "parsing" }),
        new Seed("env-setup",            "Project Environment Setup",   "Detect project stack and automatically configure the local development environment", new[] { "setup", "environment", "devtools", "onboarding" }),
        new Seed("code-walkthrough",     "Code Walkthrough",            "Generate an interactive walkthrough of a codebase feature, tracing execution flow", new[] { "code-review", "documentation", "onboarding", "architecture" }),
        new Seed("skyvern-skill",        "Skyvern Browser Automation",  "AI-powered browser automation — navigate sites, fill forms, extract structured data", new[] { "browser", "automation", "scraping", "ai" }),
    };

    /// <summary>
    /// Writes any missing claudedirectory.org skill manifests into
    /// <paramref name="skillsDirectory"/>. Returns the list of skill IDs
    /// that were newly written (does not overwrite existing files).
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureSeededAsync(string skillsDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(skillsDirectory);
        var written = new List<string>();
        foreach (var s in All)
        {
            var path = Path.Combine(skillsDirectory, s.Id + ".skill.xml");
            if (File.Exists(path)) continue;
            await File.WriteAllTextAsync(path, BuildManifest(s), Encoding.UTF8, ct);
            written.Add(s.Id);
        }
        return written;
    }

    private static string BuildManifest(Seed s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Skill xmlns=\"https://lordhelm.dev/schemas/skill-manifest/v1\">");
        sb.Append("  <Id>").Append(s.Id).AppendLine("</Id>");
        sb.AppendLine("  <Version>0.1.0</Version>");
        sb.AppendLine("  <ExecutionEnvironment>Host</ExecutionEnvironment>");
        sb.AppendLine("  <RequiresApproval>false</RequiresApproval>");
        sb.AppendLine("  <RiskTier>Read</RiskTier>");
        sb.AppendLine("  <Timeout>PT2M</Timeout>");
        sb.AppendLine("  <MinTrust>Low</MinTrust>");
        sb.Append("  <Description>").Append(XmlEscape(s.Description)).AppendLine("</Description>");
        sb.AppendLine("  <Tags>");
        foreach (var t in s.Tags)
            sb.Append("    <Tag>").Append(XmlEscape(t)).AppendLine("</Tag>");
        sb.AppendLine("  </Tags>");
        sb.AppendLine("  <ParameterSchema><![CDATA[{");
        sb.AppendLine("    \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",");
        sb.AppendLine("    \"type\": \"object\",");
        sb.AppendLine("    \"required\": [\"task\"],");
        sb.AppendLine("    \"properties\": {");
        sb.AppendLine("      \"task\": { \"type\": \"string\", \"minLength\": 1 },");
        sb.AppendLine("      \"context\": { \"type\": \"string\" }");
        sb.AppendLine("    },");
        sb.AppendLine("    \"additionalProperties\": false");
        sb.AppendLine("  }]]></ParameterSchema>");
        sb.AppendLine("</Skill>");
        return sb.ToString();
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
