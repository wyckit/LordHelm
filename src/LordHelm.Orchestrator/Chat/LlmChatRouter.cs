using System.Text.Json;
using LordHelm.Core;
using LordHelm.Orchestrator.Cortex;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Chat;

/// <summary>
/// LLM-determined routing for chat-initiated goals. Panel-endorsed shape
/// (debate-lordhelm-chat-as-dispatch-llm-routed-2026-04-21): strict JSON
/// contract, persona allow-list, explicit rationale field, refusal path
/// for out-of-scope requests, clarification path for ambiguous requests.
///
/// The safety floor (<see cref="SafetyFloor"/>) runs AFTER this router and
/// can only escalate — never de-escalate — whatever the router proposed.
/// </summary>
public sealed class LlmChatRouter : IChatRouter
{
    private readonly IExpertRegistry _experts;
    private readonly ILordHelmCortex? _cortex;
    private readonly ILogger<LlmChatRouter> _logger;

    public LlmChatRouter(
        IExpertRegistry experts,
        ILogger<LlmChatRouter> logger,
        LordHelm.Orchestrator.Cortex.ILordHelmCortex? cortex = null)
    {
        _experts = experts;
        _logger = logger;
        _cortex = cortex;
    }

    public async Task<ChatRoutingPlan> RouteAsync(
        string operatorMessage,
        IReadOnlyList<string> recentConversation,
        CancellationToken ct = default)
    {
        var router = _experts.Get("synthesiser");
        if (router is null)
        {
            _logger.LogWarning("chat router: synthesiser persona not registered — falling back to OneShot default");
            return DefaultOneShot("synthesiser missing");
        }

        // Cortex hint — surface up to 3 similar past routings so the router
        // can stay consistent with prior decisions on recurring phrasings.
        var hintBlock = "";
        if (_cortex is not null)
        {
            try
            {
                var hits = await _cortex.RecallAcrossFleetAsync(operatorMessage, k: 3, ct);
                if (hits.Count > 0)
                {
                    hintBlock = "\n\n# PRIOR CONTEXT\n" + string.Join("\n---\n",
                        hits.Select(h => Truncate(h.Text, 240)));
                }
            }
            catch { /* cortex optional */ }
        }

        var personaList = string.Join(", ", _experts.All.Select(e => e.Id));
        var prompt = $@"# ROLE
You are Lord Helm's chat router. Read the operator's message and decide how to handle it.

# CONTRACT (STRICT)
Your ONLY output is a single JSON object. No prose. No markdown fences.

# SCHEMA
{{
  ""kind"": ""OneShot""|""DecomposeAndDispatch""|""Clarify""|""Refuse"",
  ""personaHints"": string[],   // subset of [{personaList}]
  ""tier"": ""fast""|""deep""|""code""|null,
  ""modelHint"": string|null,
  ""needsPanel"": boolean,
  ""panelSize"": integer 0..5,
  ""skillHints"": string[],     // empty if no skill needed
  ""riskTier"": ""Read""|""Write""|""Delete""|""Network""|""Exec""|null,
  ""rationale"": string,
  ""clarifyingQuestion"": string|null,   // set only when kind=Clarify
  ""refusalReason"": string|null          // set only when kind=Refuse
}}

# RULES
- OneShot: single expert answers directly (chitchat, quick lookups, summarising recent state).
- DecomposeAndDispatch: multi-step goal (audit, review PR, generate report). Engage the decomposer.
- Clarify: set a single direct question into clarifyingQuestion.
- Refuse: out-of-scope or unsafe — set refusalReason.
- Conservatism: prefer OneShot for ambiguity; prefer DecomposeAndDispatch only when the message is clearly multi-step.
- Panels: only set needsPanel=true when quality-vs-cost justifies it (security review, irreversible decisions). Default false.
- Skills: pick from the skill manifest library when the task clearly needs one; otherwise empty.
{hintBlock}

# RECENT CONVERSATION
{string.Join("\n", recentConversation.TakeLast(6))}

# OPERATOR MESSAGE
{operatorMessage}

# OUTPUT
Emit the JSON now. Nothing else.";

        try
        {
            var act = await router.ActAsync(new ExpertActRequest(prompt, EstimatedContextTokens: 6000), ct);
            if (!act.Succeeded || string.IsNullOrWhiteSpace(act.Output))
            {
                _logger.LogInformation("chat router: LLM failed ({Err}) — fallback OneShot", act.Error);
                return DefaultOneShot("router call failed");
            }
            return Parse(act.Output) ?? DefaultOneShot("router output unparseable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "chat router threw — fallback OneShot");
            return DefaultOneShot("router exception: " + ex.Message);
        }
    }

    // ---- parse helpers ----

    private ChatRoutingPlan? Parse(string raw)
    {
        try
        {
            var first = raw.IndexOf('{'); var last = raw.LastIndexOf('}');
            if (first < 0 || last <= first) return null;
            var json = raw.Substring(first, last - first + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var kind = Enum.TryParse<ChatRouteKind>(root.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null,
                true, out var parsed) ? parsed : ChatRouteKind.OneShot;
            var persona = ReadStringArray(root, "personaHints");
            var tier = ReadString(root, "tier");
            var model = ReadString(root, "modelHint");
            var needsPanel = root.TryGetProperty("needsPanel", out var npEl) && npEl.ValueKind == JsonValueKind.True;
            var panelSize = root.TryGetProperty("panelSize", out var psEl) && psEl.ValueKind == JsonValueKind.Number
                ? Math.Clamp(psEl.GetInt32(), 0, 5) : (needsPanel ? 2 : 0);
            var skills = ReadStringArray(root, "skillHints");
            var riskStr = ReadString(root, "riskTier");
            RiskTier? risk = Enum.TryParse<RiskTier>(riskStr, true, out var rt) ? rt : null;
            var rationale = ReadString(root, "rationale") ?? "(no rationale)";
            var clarify = kind == ChatRouteKind.Clarify ? ReadString(root, "clarifyingQuestion") : null;
            var refusal = kind == ChatRouteKind.Refuse ? ReadString(root, "refusalReason") : null;

            // Filter persona hints to the registered allow-list.
            var known = new HashSet<string>(_experts.All.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            persona = persona.Where(p => known.Contains(p)).ToList();

            return new ChatRoutingPlan(kind, persona, tier, model, needsPanel, panelSize, skills, risk,
                rationale, clarify, refusal);
        }
        catch (JsonException) { return null; }
    }

    private static ChatRoutingPlan DefaultOneShot(string rationale) =>
        new(ChatRouteKind.OneShot, new[] { "synthesiser" }, Tier: null, ModelHint: null,
            NeedsPanel: false, PanelSize: 0, SkillHints: Array.Empty<string>(),
            RiskTier: null, Rationale: rationale);

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
