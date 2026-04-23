using System.Text;

namespace LordHelm.Skills.Seeding;

/// <summary>
/// One-time seeder that drops the <a href="https://github.com/openai/skills/tree/main/skills/.curated">
/// openai/skills .curated</a> catalog into the skills/ directory as
/// <c>.skill.xml</c> manifests. Non-destructive: never overwrites an
/// existing file.
///
/// Ids use a <c>openai-</c> prefix so they don't collide with the
/// claudedirectory seed (e.g. both have a <c>playwright</c> entry). All
/// entries default to Host / Read / Low-trust; re-classify via /skills
/// when a specific skill needs Docker / EXEC.
/// </summary>
public static class OpenAiCuratedSkillSeed
{
    public sealed record Seed(string Id, string Name, string Description, string[] Tags);

    public static IReadOnlyList<Seed> All { get; } = new[]
    {
        new Seed("openai-aspnet-core",                      "ASP.NET Core",                       "Build ASP.NET Core web applications and APIs following framework best practices", new[] { "dotnet", "web", "api", "backend" }),
        new Seed("openai-chatgpt-apps",                     "ChatGPT Apps",                       "Build applications that integrate with ChatGPT via the OpenAI API", new[] { "chatgpt", "openai", "api", "apps" }),
        new Seed("openai-cli-creator",                      "CLI Creator",                        "Author command-line interfaces with argument parsing, subcommands, and help text", new[] { "cli", "tooling", "unix" }),
        new Seed("openai-cloudflare-deploy",                "Cloudflare Deploy",                  "Deploy workers, pages, and edge services to Cloudflare", new[] { "deploy", "cloudflare", "edge", "serverless" }),
        new Seed("openai-develop-web-game",                 "Develop Web Game",                   "Build browser-based games with HTML5 canvas, WebGL, or engine integrations", new[] { "games", "web", "canvas", "webgl" }),
        new Seed("openai-doc",                              "Documentation Author",               "Author technical documentation with consistent structure and examples", new[] { "documentation", "writing", "reference" }),
        new Seed("openai-figma-code-connect-components",    "Figma Code Connect Components",      "Map Figma components to code components for bidirectional design-dev sync", new[] { "figma", "design", "code-connect", "components" }),
        new Seed("openai-figma-create-design-system-rules", "Figma Design System Rules",          "Define and enforce design-system rules inside Figma", new[] { "figma", "design-system", "rules" }),
        new Seed("openai-figma-create-new-file",            "Figma Create New File",              "Scaffold new Figma files with proper page structure and variables", new[] { "figma", "scaffold", "design" }),
        new Seed("openai-figma-generate-design",            "Figma Generate Design",              "Generate UI designs in Figma from requirements or sketches", new[] { "figma", "design", "generation" }),
        new Seed("openai-figma-generate-library",           "Figma Generate Library",             "Build component libraries with variants and tokens in Figma", new[] { "figma", "library", "components" }),
        new Seed("openai-figma-implement-design",           "Figma Implement Design",             "Translate a Figma design into working front-end code", new[] { "figma", "implementation", "frontend" }),
        new Seed("openai-figma-use",                        "Figma Use",                          "Navigate and use Figma files programmatically", new[] { "figma", "design", "navigation" }),
        new Seed("openai-figma",                            "Figma",                              "General Figma operations — files, frames, components, export", new[] { "figma", "design" }),
        new Seed("openai-frontend-skill",                   "Frontend Skill",                     "Front-end engineering: HTML, CSS, components, responsive layout, accessibility", new[] { "frontend", "html", "css", "a11y" }),
        new Seed("openai-gh-address-comments",              "GitHub Address Comments",            "Triage and address PR review comments systematically", new[] { "github", "review", "pr", "workflow" }),
        new Seed("openai-gh-fix-ci",                        "GitHub Fix CI",                      "Diagnose and repair GitHub Actions CI failures", new[] { "github", "ci", "actions", "debugging" }),
        new Seed("openai-jupyter-notebook",                 "Jupyter Notebook",                   "Author and analyze Jupyter notebooks for data science and research", new[] { "jupyter", "notebook", "data-science" }),
        new Seed("openai-linear",                           "Linear",                             "Linear issue tracking: create, triage, link, and move issues", new[] { "linear", "issues", "workflow", "pm" }),
        new Seed("openai-netlify-deploy",                   "Netlify Deploy",                     "Deploy sites and functions to Netlify with edge + redirect config", new[] { "deploy", "netlify", "jamstack" }),
        new Seed("openai-notion-knowledge-capture",         "Notion Knowledge Capture",           "Capture and structure knowledge as Notion pages and databases", new[] { "notion", "knowledge", "documentation" }),
        new Seed("openai-notion-meeting-intelligence",      "Notion Meeting Intelligence",        "Summarize meetings and surface action items into Notion", new[] { "notion", "meetings", "summarization" }),
        new Seed("openai-notion-research-documentation",    "Notion Research Documentation",      "Organize research output into Notion knowledge bases", new[] { "notion", "research", "documentation" }),
        new Seed("openai-notion-spec-to-implementation",    "Notion Spec to Implementation",      "Turn a Notion spec into a concrete implementation plan", new[] { "notion", "spec", "implementation", "planning" }),
        new Seed("openai-openai-docs",                      "OpenAI Docs",                        "Navigate and author OpenAI API documentation", new[] { "openai", "api", "documentation" }),
        new Seed("openai-pdf",                              "PDF",                                "Read, parse, and author PDF documents", new[] { "pdf", "documents", "parsing" }),
        new Seed("openai-playwright-interactive",           "Playwright Interactive",             "Interactive Playwright session — inspect, click, and verify live", new[] { "playwright", "browser", "interactive" }),
        new Seed("openai-playwright",                       "Playwright",                         "Browser automation and testing with Playwright", new[] { "playwright", "browser", "testing", "automation" }),
        new Seed("openai-render-deploy",                    "Render Deploy",                      "Deploy web services, workers, and databases to Render", new[] { "deploy", "render", "backend" }),
        new Seed("openai-screenshot",                       "Screenshot",                         "Capture and annotate screenshots programmatically", new[] { "screenshot", "capture", "visual" }),
        new Seed("openai-security-best-practices",          "Security Best Practices",            "Apply OWASP and industry security best practices across an application", new[] { "security", "owasp", "best-practices" }),
        new Seed("openai-security-ownership-map",           "Security Ownership Map",             "Build a map of security-critical code and its owners", new[] { "security", "ownership", "governance" }),
        new Seed("openai-security-threat-model",            "Security Threat Model",              "Produce a STRIDE/PASTA threat model for a system", new[] { "security", "threat-modeling", "stride" }),
        new Seed("openai-sentry",                           "Sentry",                             "Sentry error tracking: triage, alert rules, source maps, release tracking", new[] { "sentry", "observability", "errors" }),
        new Seed("openai-sora",                             "Sora",                               "OpenAI Sora video generation integration", new[] { "sora", "video", "generation", "openai" }),
        new Seed("openai-speech",                           "Speech",                             "Speech synthesis and recognition integrations", new[] { "speech", "tts", "stt", "audio" }),
        new Seed("openai-transcribe",                       "Transcribe",                         "Transcribe audio and video to text with speaker labels and timestamps", new[] { "transcription", "audio", "whisper" }),
        new Seed("openai-vercel-deploy",                    "Vercel Deploy",                      "Deploy Next.js and edge apps to Vercel", new[] { "deploy", "vercel", "nextjs" }),
        new Seed("openai-winui-app",                        "WinUI App",                          "Build Windows desktop apps with WinUI 3 and WinAppSDK", new[] { "winui", "windows", "desktop", "dotnet" }),
        new Seed("openai-yeet",                             "Yeet",                               "Rapid-deploy primitive — ship a change with minimum ceremony", new[] { "deploy", "shipping", "productivity" }),
    };

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
