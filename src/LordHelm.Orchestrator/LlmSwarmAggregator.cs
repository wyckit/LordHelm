using System.Text;
using LordHelm.Providers;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

/// <summary>
/// LLM-driven merge of N parallel swarm member outputs into one coherent answer.
/// Prompt shape comes from council session `lord-helm-gaps-abcd-2026-04-21`:
/// preserve every citation, surface conflicts explicitly, drop redundancy, always
/// synthesise (never majority-vote). Fallback to <see cref="ConcatSwarmAggregator"/>
/// on provider error so swarm tasks never silently lose output.
/// </summary>
public sealed class LlmSwarmAggregator : ISwarmAggregator
{
    private readonly IProviderOrchestrator _providers;
    private readonly IExpertRegistry _experts;
    private readonly ConcatSwarmAggregator _fallback = new();
    private readonly ILogger<LlmSwarmAggregator> _logger;
    public string Vendor { get; }
    public string Model { get; }
    public int MaxTokens { get; }

    public LlmSwarmAggregator(
        IProviderOrchestrator providers,
        IExpertRegistry experts,
        ILogger<LlmSwarmAggregator> logger,
        string vendor = "claude",
        string model = "claude-opus-4-7",
        int maxTokens = 1024)
    {
        _providers = providers;
        _experts = experts;
        _logger = logger;
        Vendor = vendor;
        Model = model;
        MaxTokens = maxTokens;
    }

    public async Task<string> AggregateAsync(TaskNode task, IReadOnlyList<SwarmMemberOutput> outputs, CancellationToken ct = default)
    {
        if (outputs.Count == 0) return "(no members to aggregate)";
        if (outputs.Count == 1) return outputs[0].Output;

        var prompt = BuildPrompt(task, outputs);
        var contextEstimate = Math.Max(4000, prompt.Length / 4);

        // Same IExpert-first pattern as LlmSynthesizer: the synthesiser persona
        // owns both "merge N swarm outputs" and "merge N dag leaves" — there is
        // no semantic reason for two separate code paths. Expert failure falls
        // through to the provider orchestrator, then to concat.
        var expert = _experts.Get("synthesiser");
        if (expert is not null)
        {
            var act = await expert.ActAsync(new ExpertActRequest(
                Task: prompt,
                EstimatedContextTokens: contextEstimate), ct);
            if (act.Succeeded && !string.IsNullOrWhiteSpace(act.Output))
                return act.Output.Trim();
            _logger.LogWarning("Synthesiser expert failed ({Err}); falling back to provider orchestrator.",
                act.Error ?? "empty");
        }

        var response = await _providers.GenerateWithFailoverAsync(
            preferredVendor: Vendor,
            modelOverride: Model,
            prompt: prompt,
            hint: new ProviderTaskHint(TaskKind: task.TaskKind ?? "summarisation",
                EstimatedContextTokens: contextEstimate),
            maxTokens: MaxTokens,
            temperature: 0.1f,
            ct: ct);

        if (response.Error is not null || string.IsNullOrWhiteSpace(response.AssistantMessage))
        {
            _logger.LogWarning("LlmSwarmAggregator failed ({Err}); falling back to concat.",
                response.Error?.Message ?? "empty response");
            return await _fallback.AggregateAsync(task, outputs, ct);
        }
        return response.AssistantMessage.Trim();
    }

    public static string BuildPrompt(TaskNode task, IReadOnlyList<SwarmMemberOutput> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ROLE");
        sb.AppendLine("You are a synthesis judge. Merge the following expert responses into ONE coherent answer.");
        sb.AppendLine();
        sb.AppendLine("# RULES (apply in order)");
        sb.AppendLine("1. CITATIONS: Preserve every concrete fact, number, code snippet, file:line, or named reference from any member. Tag the source as [MemberN].");
        sb.AppendLine("2. CONFLICTS: If two or more members assert contradictory claims, emit a `## Conflicts` subsection listing each conflict as: `Conflict: [MemberX] says A; [MemberY] says B — unresolved.`");
        sb.AppendLine("3. REDUNDANCY: Drop content that is semantically identical across members; keep the clearest phrasing only once.");
        sb.AppendLine("4. COHERENCE: Output one continuous integrated answer. Do NOT list per-member summaries.");
        sb.AppendLine("5. FIDELITY: Do not invent content not present in any member response.");
        sb.AppendLine();
        sb.AppendLine("# TASK");
        sb.AppendLine(task.Goal);
        sb.AppendLine();
        sb.AppendLine("# MEMBERS");
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            sb.AppendLine($"--- Member {i + 1} [persona: {m.Persona} | vendor: {m.Vendor}] ---");
            sb.AppendLine(m.Output.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("# OUTPUT");
        sb.AppendLine("Produce the merged answer now. Markdown allowed.");
        return sb.ToString();
    }
}
