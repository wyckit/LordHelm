using System.Text.RegularExpressions;

namespace LordHelm.Orchestrator.ModelDiscovery;

public interface IModelListParser
{
    IReadOnlyList<ProbedModel> Parse(string raw);
}

/// <summary>
/// Parses the numbered-list shape every interactive CLI uses for its
/// <c>/model</c> slash command. Handles "current"/"default" tags and
/// right-aligned descriptions. Example lines matched:
/// <code>
/// 1. gpt-5.4 (current)   Latest frontier agentic coding model.
/// 2. gpt-5.2-codex       Frontier agentic coding model.
/// &#x203A; 1. claude-opus-4-7 (current)   Deepest reasoning; highest cost.
/// </code>
/// Tier is inferred from the description — fast / mini / haiku / flash → Fast,
/// codex / code → Code, otherwise Deep.
/// </summary>
public sealed class NumberedListModelParser : IModelListParser
{
    private static readonly Regex LineRegex = new(
        @"^[\s›*\-\>]*\d+\.\s+(?<id>\S+)(?:\s*\([^\)]+\))?\s*(?<desc>.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public IReadOnlyList<ProbedModel> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<ProbedModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ProbedModel>();
        foreach (Match m in LineRegex.Matches(raw))
        {
            // Strip wrapping backticks / quotes / asterisks that LLM
            // markdown-formatted lists love to include around model ids.
            // Without this, "1. `gpt-5.4`" lands in the catalog as the
            // literal id "`gpt-5.4`" which then can't be dispatched.
            var id = m.Groups["id"].Value.Trim().Trim('`', '"', '\'', '*');
            var desc = m.Groups["desc"].Value.Trim();
            if (id.Length == 0 || !LooksLikeModelId(id) || !seen.Add(id)) continue;
            list.Add(new ProbedModel(id, desc, InferTier(id, desc)));
        }
        return list;
    }

    // Reject obviously-wrong captures: pure prose, single tokens that look
    // like English words, anything missing a digit or a hyphen (real model
    // ids are kebab-case with version digits — claude-opus-4-7, gpt-5.4,
    // gemini-2.5-flash). Prevents `Output`, `none`, `unknown`, etc. from
    // hallucinated lines getting upserted as "models".
    private static bool LooksLikeModelId(string id)
    {
        if (id.Length < 3 || id.Length > 80) return false;
        if (!id.Any(char.IsDigit) && !id.Contains('-')) return false;
        if (id.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static LordHelm.Orchestrator.ModelTier InferTier(string id, string desc)
    {
        var idLower = id.ToLowerInvariant();
        var descLower = desc.ToLowerInvariant();

        // Fast-tier signals always win — `gpt-5.1-codex-mini` is Fast, not Code.
        if (idLower.Contains("mini") || idLower.Contains("haiku") ||
            idLower.Contains("flash") || idLower.Contains("-nano") ||
            descLower.Contains("smaller") || descLower.Contains("cheaper"))
            return ModelTier.Fast;

        // `-max` / "flagship" / "deep reasoning" → Deep even if id has "codex".
        // This keeps `gpt-5.1-codex-max` in Deep.
        if (idLower.Contains("max") ||
            descLower.Contains("flagship for deep") ||
            descLower.Contains("deep and fast reasoning"))
            return ModelTier.Deep;

        // Explicit codex-tagged IDs (e.g. `gpt-5.2-codex`, `gpt-5-codex`) are Code.
        // Description-only "coding" no longer triggers Code — too many non-codex
        // flagships describe themselves as "agentic coding".
        if (idLower.Contains("codex")) return ModelTier.Code;

        return ModelTier.Deep;
    }
}
