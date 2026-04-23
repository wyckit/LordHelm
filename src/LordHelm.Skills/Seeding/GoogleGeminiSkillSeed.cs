using System.Text;

namespace LordHelm.Skills.Seeding;

/// <summary>
/// One-time seeder that drops the <a href="https://github.com/google-gemini/gemini-skills/tree/main/skills">
/// google-gemini/gemini-skills</a> catalogue into the skills/ directory as
/// <c>.skill.xml</c> manifests. Non-destructive: never overwrites an
/// existing file.
///
/// Ids are prefixed <c>gemini-</c> so they don't collide with the
/// claudedirectory / openai seed catalogues. All entries default to
/// Host / Read / Low-trust — they're guidance manifests for Gemini SDK
/// usage, not sandboxed executors.
/// </summary>
public static class GoogleGeminiSkillSeed
{
    public sealed record Seed(string Id, string Name, string Description, string[] Tags);

    public static IReadOnlyList<Seed> All { get; } = new[]
    {
        new Seed("gemini-gemini-api-dev",
            "Gemini API Development",
            "Use when building applications with Gemini models, Gemini API, working with multimodal content (text, images, audio, video), function calling, or structured outputs. Covers SDK usage (google-genai for Python, @google/genai for JavaScript/TypeScript, google-genai for Java, google.golang.org/genai for Go), model selection, and API capabilities.",
            new[] { "gemini", "google", "api", "sdk", "multimodal" }),

        new Seed("gemini-interactions-api",
            "Gemini Interactions API",
            "Use when writing code that calls the Gemini API for text generation, multi-turn chat, multimodal understanding, image generation, streaming, background research, function calling, structured output, or migrating from generateContent. Covers the Interactions API in Python and TypeScript.",
            new[] { "gemini", "google", "interactions", "chat", "streaming" }),

        new Seed("gemini-live-api-dev",
            "Gemini Live API Development",
            "Use when building real-time, bidirectional streaming applications with the Gemini Live API. Covers WebSocket-based audio/video/text streaming, Voice Activity Detection, native audio, function calling, session management, and ephemeral tokens for client-side auth. SDKs: google-genai (Python), @google/genai (JavaScript/TypeScript).",
            new[] { "gemini", "google", "live", "websocket", "realtime", "voice" }),

        new Seed("gemini-vertex-ai-api-dev",
            "Vertex AI API Development",
            "Guides usage of the Gemini API on Google Cloud Vertex AI with the Gen AI SDK. Use when the user is in an enterprise environment or explicitly mentions Vertex AI. Covers SDK usage (Python, JS/TS, Go, Java, C#), Live API, tools, multimedia generation, context caching, and batch prediction. Requires active Google Cloud credentials and Vertex AI API enabled.",
            new[] { "gemini", "google", "vertex-ai", "enterprise", "cloud", "batch" }),
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
