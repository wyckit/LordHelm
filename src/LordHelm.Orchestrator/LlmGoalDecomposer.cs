using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LordHelm.Core;
using LordHelm.Providers;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record LlmDecomposerOptions
{
    public string PreferredVendor { get; init; } = "claude";
    public int MaxTokens { get; init; } = 1536;
    public float Temperature { get; init; } = 0.1f;
    public int MaxTasks { get; init; } = 12;
    public bool FallbackToPassthrough { get; init; } = true;
    public int MaxParseRetries { get; init; } = 1;
}

/// <summary>
/// Decomposes a goal into a TaskNode DAG by prompting an LLM through the provider
/// orchestrator. The panel-resolved design (council session
/// `lord-helm-gaps-abcd-2026-04-21`) enforces: JSON-only output contract, closed
/// enums for `swarmStrategy`, persona catalogue passed as id+one-liner only, and
/// parse-failure handling via retry (never server-side text repair).
/// </summary>
public sealed class LlmGoalDecomposer : IGoalDecomposer
{
    private static readonly Regex FencedJson = new(
        @"^```(?:json)?\s*|\s*```\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IProviderOrchestrator _providers;
    private readonly LlmDecomposerOptions _options;
    private readonly ExpertDirectory? _directory;
    private readonly ILogger<LlmGoalDecomposer> _logger;

    public LlmGoalDecomposer(
        IProviderOrchestrator providers,
        LlmDecomposerOptions options,
        ILogger<LlmGoalDecomposer> logger,
        ExpertDirectory? directory = null)
    {
        _providers = providers;
        _options = options;
        _directory = directory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskNode>> DecomposeAsync(
        string goal,
        IReadOnlyList<SkillManifest> availableSkills,
        CancellationToken ct = default)
    {
        var personas = _directory?.All() ?? Array.Empty<ExpertPersona>();
        var prompt = BuildPrompt(goal, availableSkills, personas, _options.MaxTasks);

        string? rawResponse = null;
        for (int attempt = 0; attempt <= _options.MaxParseRetries; attempt++)
        {
            var response = await _providers.GenerateWithFailoverAsync(
                _options.PreferredVendor, null, prompt,
                new ProviderTaskHint(TaskKind: "reasoning",
                    EstimatedContextTokens: Math.Max(4000, prompt.Length / 4)),
                _options.MaxTokens, _options.Temperature, ct);

            if (response.Error is not null || string.IsNullOrWhiteSpace(response.AssistantMessage))
            {
                _logger.LogWarning("Decomposer attempt {N} failed ({Err})", attempt,
                    response.Error?.Message ?? "empty");
                continue;
            }

            rawResponse = response.AssistantMessage;
            if (TryParseTasks(rawResponse, goal, out var tasks, out var reason))
            {
                try
                {
                    TaskDag.TopoSort(tasks);
                    _logger.LogInformation("Decomposed {N} tasks (attempt {A})", tasks.Count, attempt);
                    return tasks;
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning("Decomposer produced cyclic DAG: {Msg}", ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("Decomposer attempt {N} unparseable: {Reason}", attempt, reason);
            }
        }

        _logger.LogWarning("Decomposer exhausted retries; falling back to passthrough.");
        return Fallback(goal);
    }

    public static string BuildPrompt(
        string goal,
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<ExpertPersona> personas,
        int maxTasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ROLE");
        sb.AppendLine("You are Lord Helm's goal decomposer.");
        sb.AppendLine();
        sb.AppendLine("# CONTRACT (STRICT)");
        sb.AppendLine("Your ONLY output is a single JSON object. No prose. No markdown fences. No explanation.");
        sb.AppendLine("Any non-JSON output is a protocol error and will be rejected.");
        sb.AppendLine();
        sb.AppendLine("# SCHEMA");
        sb.AppendLine("{\"tasks\":[ {");
        sb.AppendLine("  \"id\": string (kebab-case, unique within the set),");
        sb.AppendLine("  \"goal\": string (imperative, single sentence),");
        sb.AppendLine("  \"dependsOn\": string[] (other ids; may be empty; no cycles),");
        sb.AppendLine("  \"persona\": string|null (one of the persona ids below, or null),");
        sb.AppendLine("  \"skill\": string|null (one of the skill ids below, or null for synthesis),");
        sb.AppendLine("  \"preferredVendor\": \"claude\"|\"gemini\"|\"codex\"|null,");
        sb.AppendLine("  \"tier\": \"fast\"|\"deep\"|\"code\"|null,");
        sb.AppendLine("  \"modelHint\": string|null (specific model id to downshift cheap sub-tasks, e.g. \"claude-haiku-4-5\"),");
        sb.AppendLine("  \"swarmSize\": integer 1..5|null,");
        sb.AppendLine("  \"swarmStrategy\": \"Single\"|\"Redundant\"|\"Diverse\"|null");
        sb.AppendLine("} ] }");
        sb.AppendLine();
        sb.AppendLine($"# CONSTRAINTS");
        sb.AppendLine($"- At most {maxTasks} tasks.");
        sb.AppendLine("- Ids must be unique and kebab-case.");
        sb.AppendLine("- dependsOn entries must reference other ids in THIS object; never dangling.");
        sb.AppendLine("- When a task truly integrates outputs of other tasks, set skill=null and persona=\"synthesiser\".");
        sb.AppendLine("- Use swarmSize>1 ONLY when diverse perspectives improve quality (e.g. code review, security audit). Default to 1.");

        sb.AppendLine();
        sb.AppendLine("# PERSONA CATALOGUE (id -- summary)");
        if (personas.Count == 0)
        {
            sb.AppendLine("  (none registered — leave persona=null)");
        }
        else
        {
            foreach (var p in personas)
                sb.AppendLine($"  {p.Id} -- {p.Name}: preferred_vendor={p.PreferredVendor}");
        }

        sb.AppendLine();
        sb.AppendLine("# SKILL CATALOGUE (id -- env / risk / version)");
        if (skills.Count == 0)
        {
            sb.AppendLine("  (none loaded — every task must set skill=null)");
        }
        else
        {
            foreach (var s in skills.Take(32))
                sb.AppendLine($"  {s.Id} -- env={s.ExecEnv} risk={s.RiskTier} v{s.Version}");
        }

        sb.AppendLine();
        sb.AppendLine("# TIER GUIDANCE");
        sb.AppendLine("- fast: one-shot classification, short summarisation, simple extraction.");
        sb.AppendLine("- deep: multi-step reasoning, planning, debugging, review.");
        sb.AppendLine("- code: code synthesis, refactoring, transformation.");
        sb.AppendLine();
        sb.AppendLine("# GOAL");
        sb.AppendLine(goal);
        sb.AppendLine();
        sb.AppendLine("# OUTPUT");
        sb.AppendLine("Emit the JSON object now. Nothing else.");
        return sb.ToString();
    }

    public static bool TryParseTasks(string rawResponse, string goal,
        out IReadOnlyList<TaskNode> tasks, out string? reason)
    {
        tasks = Array.Empty<TaskNode>();
        reason = null;

        // Council: never repair non-JSON; but permit a fenced block as a graceful
        // concession since Claude sometimes wraps output despite instructions.
        var cleaned = FencedJson.Replace(rawResponse.Trim(), string.Empty).Trim();
        var first = cleaned.IndexOf('{');
        var last = cleaned.LastIndexOf('}');
        if (first < 0 || last <= first)
        {
            reason = "no JSON object in response";
            return false;
        }
        var json = cleaned.Substring(first, last - first + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array)
            {
                reason = "missing 'tasks' array";
                return false;
            }

            var nodes = new List<TaskNode>();
            foreach (var t in tasksEl.EnumerateArray())
            {
                if (!t.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    reason = "task missing id";
                    return false;
                }
                var id = idEl.GetString()!;
                var taskGoal = t.TryGetProperty("goal", out var gEl) ? gEl.GetString() ?? "" : "";
                var deps = t.TryGetProperty("dependsOn", out var dEl) && dEl.ValueKind == JsonValueKind.Array
                    ? dEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>();
                var persona = TryReadString(t, "persona");
                var skill = TryReadString(t, "skill");
                var vendor = TryReadString(t, "preferredVendor");
                var swarmSize = t.TryGetProperty("swarmSize", out var szEl) && szEl.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(szEl.GetInt32(), 1, 5)
                    : 1;
                var swarmStrategy = SwarmStrategy.Single;
                if (t.TryGetProperty("swarmStrategy", out var ssEl) && ssEl.ValueKind == JsonValueKind.String)
                {
                    _ = Enum.TryParse<SwarmStrategy>(ssEl.GetString(), ignoreCase: true, out swarmStrategy);
                }

                // Map the decomposer's "tier" hint into a router-friendly task-kind.
                // fast/deep → reasoning; code → code; anything else → null (router
                // falls back to generic match).
                var tier = TryReadString(t, "tier")?.ToLowerInvariant();
                var taskKind = tier switch
                {
                    "code" => "code",
                    "deep" or "fast" => "reasoning",
                    _ => null
                };
                var modelHint = TryReadString(t, "modelHint");

                nodes.Add(new TaskNode(id, taskGoal, deps, persona, skill, vendor, swarmSize, swarmStrategy,
                    TaskKind: taskKind, ModelHint: modelHint));
            }

            if (nodes.Count == 0)
            {
                reason = "empty tasks array";
                return false;
            }

            var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != nodes.Count) { reason = "duplicate task ids"; return false; }
            foreach (var n in nodes)
            {
                foreach (var d in n.DependsOn)
                {
                    if (!ids.Contains(d)) { reason = $"dangling dependsOn '{d}' in '{n.Id}'"; return false; }
                }
            }
            tasks = nodes;
            return true;
        }
        catch (JsonException ex)
        {
            reason = "json parse error: " + ex.Message;
            return false;
        }
    }

    private static string? TryReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static IReadOnlyList<TaskNode> Fallback(string goal) =>
        new[] { new TaskNode("root", goal, Array.Empty<string>()) };
}
