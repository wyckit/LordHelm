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
    public int MaxTokens { get; init; } = 1024;
    public float Temperature { get; init; } = 0.1f;
    public int MaxTasks { get; init; } = 12;
    public bool FallbackToPassthrough { get; init; } = true;
}

/// <summary>
/// Decomposes a natural-language goal into a TaskNode DAG by prompting an LLM
/// through <see cref="IProviderOrchestrator"/>. Uses a JSON-constrained prompt,
/// strips any markdown code fence wrapping, validates the response shape, and
/// falls back to a single-node passthrough when the model output cannot be
/// parsed into a sensible DAG. The fallback preserves availability when claude /
/// gemini / codex are offline or mis-configured.
/// </summary>
public sealed class LlmGoalDecomposer : IGoalDecomposer
{
    private static readonly Regex FencedJson = new(
        @"^```(?:json)?\s*|\s*```\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IProviderOrchestrator _providers;
    private readonly LlmDecomposerOptions _options;
    private readonly ILogger<LlmGoalDecomposer> _logger;

    public LlmGoalDecomposer(
        IProviderOrchestrator providers,
        LlmDecomposerOptions options,
        ILogger<LlmGoalDecomposer> logger)
    {
        _providers = providers;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskNode>> DecomposeAsync(
        string goal,
        IReadOnlyList<SkillManifest> availableSkills,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(goal, availableSkills, _options.MaxTasks);

        var response = await _providers.GenerateWithFailoverAsync(
            _options.PreferredVendor,
            modelOverride: null,
            prompt: prompt,
            maxTokens: _options.MaxTokens,
            temperature: _options.Temperature,
            ct);

        if (response.Error is not null || string.IsNullOrWhiteSpace(response.AssistantMessage))
        {
            _logger.LogWarning("LLM decomposition failed ({Err}); falling back to passthrough.",
                response.Error?.Message ?? "empty response");
            return Fallback(goal);
        }

        if (!TryParseTasks(response.AssistantMessage, goal, out var tasks, out var reason))
        {
            _logger.LogWarning("LLM decomposition output unparseable ({Reason}); falling back.", reason);
            return Fallback(goal);
        }

        try
        {
            TaskDag.TopoSort(tasks);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("LLM produced a cyclic DAG ({Msg}); falling back.", ex.Message);
            return Fallback(goal);
        }

        _logger.LogInformation("LLM decomposed goal into {Count} tasks via {Vendor}", tasks.Count, _options.PreferredVendor);
        return tasks;
    }

    internal static string BuildPrompt(string goal, IReadOnlyList<SkillManifest> skills, int maxTasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Lord Helm's goal decomposer.");
        sb.AppendLine("Break the user's goal into a minimal dependency tree of sub-tasks.");
        sb.AppendLine($"Produce at most {maxTasks} tasks. Every task must reference either a known skill or have no skill.");
        sb.AppendLine();
        sb.AppendLine("AVAILABLE SKILLS (id -- description):");
        if (skills.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var s in skills.Take(32))
                sb.AppendLine($"  {s.Id} -- env={s.ExecEnv} risk={s.RiskTier} v{s.Version}");
        }
        sb.AppendLine();
        sb.AppendLine($"GOAL: {goal}");
        sb.AppendLine();
        sb.AppendLine("Respond with exactly one JSON object with this shape, and no prose:");
        sb.AppendLine("""{"tasks":[{"id":"kebab-case-id","goal":"what this sub-task does","dependsOn":["other-id", ...],"skill":"optional-skill-id"}]}""");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ids must be unique and kebab-case.");
        sb.AppendLine("- dependsOn entries must reference other ids in the same object; no cycles.");
        sb.AppendLine("- omit the \"skill\" field when a task is a pure synthesis / aggregation step.");
        sb.AppendLine("- output ONLY the JSON object, no markdown, no commentary.");
        return sb.ToString();
    }

    internal static bool TryParseTasks(string rawResponse, string goal,
        out IReadOnlyList<TaskNode> tasks, out string? reason)
    {
        tasks = Array.Empty<TaskNode>();
        reason = null;

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
                nodes.Add(new TaskNode(id, taskGoal, deps));
            }

            if (nodes.Count == 0)
            {
                reason = "empty tasks array";
                return false;
            }

            var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != nodes.Count)
            {
                reason = "duplicate task ids";
                return false;
            }
            foreach (var n in nodes)
            {
                foreach (var d in n.DependsOn)
                {
                    if (!ids.Contains(d))
                    {
                        reason = $"dangling dependsOn reference '{d}' in task '{n.Id}'";
                        return false;
                    }
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

    private static IReadOnlyList<TaskNode> Fallback(string goal) =>
        new[] { new TaskNode("root", goal, Array.Empty<string>()) };
}
