using System.Text.Json;
using System.Text.RegularExpressions;
using LordHelm.Orchestrator;

namespace LordHelm.Web.Layout;

public sealed record LayoutPreset(string Key, string Title, IReadOnlyList<LayoutEntry> Entries);

/// <summary>
/// Panel-endorsed strategy: regex matcher runs first (free, instant) for the
/// five known preset phrases from the design handoff; on miss, the prompt is
/// routed to the synthesiser IExpert which must return a strict JSON array
/// of {id, columns, rows} — anything else is rejected and we fall back to
/// the default layout.
/// </summary>
public sealed class LayoutPresetResolver
{
    private readonly IExpertRegistry _experts;

    public LayoutPresetResolver(IExpertRegistry experts) { _experts = experts; }

    public static IReadOnlyList<LayoutPreset> KnownPresets { get; } = new[]
    {
        new LayoutPreset("incident", "Incident response", new[]
        {
            new LayoutEntry("alerts",    4, 3),
            new LayoutEntry("topology",  5, 3),
            new LayoutEntry("active",    3, 3),
            new LayoutEntry("health",    4, 2),
            new LayoutEntry("approvals", 4, 2),
            new LayoutEntry("fleet",     4, 2),
        }),
        new LayoutPreset("cost", "Cost & spend view", new[]
        {
            new LayoutEntry("kpis",     6, 2),
            new LayoutEntry("gantt",    6, 2),
            new LayoutEntry("fleet",    4, 3),
            new LayoutEntry("active",   5, 3),
            new LayoutEntry("health",   3, 3),
        }),
        new LayoutPreset("focus", "Focus on active agent", new[]
        {
            new LayoutEntry("active",   8, 5),
            new LayoutEntry("fleet",    4, 3),
            new LayoutEntry("recent",   4, 2),
        }),
        new LayoutPreset("overnight", "Overnight recap", new[]
        {
            new LayoutEntry("recent",  5, 3),
            new LayoutEntry("gantt",   7, 3),
            new LayoutEntry("kpis",    6, 2),
            new LayoutEntry("alerts",  6, 2),
        }),
        new LayoutPreset("approval", "Approvals queue", new[]
        {
            new LayoutEntry("approvals", 6, 4),
            new LayoutEntry("active",    6, 4),
            new LayoutEntry("fleet",     6, 2),
            new LayoutEntry("recent",    6, 2),
        }),
    };

    private static readonly Dictionary<string, Regex> _regexes = new()
    {
        ["incident"]  = new Regex(@"incident|alert|block|fire|break|fail|error|issue", RegexOptions.IgnoreCase),
        ["cost"]      = new Regex(@"spend|cost|budget|token|money|bill|\$",             RegexOptions.IgnoreCase),
        ["focus"]     = new Regex(@"focus|single|drill|deep|zoom|detail",               RegexOptions.IgnoreCase),
        ["overnight"] = new Regex(@"overnight|last night|yesterday|recap|changed|what happened", RegexOptions.IgnoreCase),
        ["approval"]  = new Regex(@"approv|review|waiting on me|need (me|you)",         RegexOptions.IgnoreCase),
    };

    public LayoutPreset? MatchByRegex(string prompt)
    {
        foreach (var (key, rx) in _regexes)
        {
            if (rx.IsMatch(prompt))
                return KnownPresets.First(p => p.Key == key);
        }
        return null;
    }

    /// <summary>
    /// Strict-JSON contract prompt routed through the synthesiser expert.
    /// Returns null on any parse/validation failure so the caller can fall
    /// back to regex or default layout.
    /// </summary>
    public async Task<LayoutPreset?> AskExpertAsync(string prompt, CancellationToken ct = default)
    {
        var expert = _experts.Get("synthesiser");
        if (expert is null) return null;

        var body = $@"# ROLE
You are Lord Helm's layout architect.

# CONTRACT (STRICT)
Your ONLY output is a single JSON array. No prose. No markdown fences. No explanation.

# SCHEMA
[ {{""id"": ""<widget-id>"", ""columns"": <1..12>, ""rows"": <1..5>}} ]

# VALID WIDGET IDS
topology, fleet, active, kpis, gantt, queue, alerts, approvals, recent, health, providers

# CONSTRAINTS
- Between 3 and 8 entries.
- Columns must be 1-12, rows 1-5.
- Prefer placing topology/active larger when the prompt implies centrepiece focus.

# OPERATOR PROMPT
{prompt}

# OUTPUT
Emit the JSON array now. Nothing else.";

        try
        {
            var act = await expert.ActAsync(new ExpertActRequest(body, EstimatedContextTokens: 4000), ct);
            if (!act.Succeeded || string.IsNullOrWhiteSpace(act.Output)) return null;

            var text = act.Output.Trim();
            var first = text.IndexOf('['); var last = text.LastIndexOf(']');
            if (first < 0 || last <= first) return null;
            var json = text.Substring(first, last - first + 1);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var known = new HashSet<string>(new[]
            { "topology","fleet","active","kpis","gantt","queue","alerts","approvals","recent","health","providers","artifacts" });

            var entries = new List<LayoutEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id  = el.TryGetProperty("id",      out var i) ? i.GetString() : null;
                var col = el.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                var row = el.TryGetProperty("rows",    out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                if (string.IsNullOrWhiteSpace(id) || !known.Contains(id) || col is < 1 or > 12 || row is < 1 or > 5) continue;
                entries.Add(new LayoutEntry(id, col, row));
            }
            if (entries.Count is < 3 or > 8) return null;
            return new LayoutPreset("llm", "LLM-generated layout", entries);
        }
        catch { return null; }
    }
}
