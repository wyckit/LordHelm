using LordHelm.Providers;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record SynthesisRequest(
    string Goal,
    IReadOnlyList<TaskNode> Dag,
    IReadOnlyDictionary<string, string> NodeOutputs);

/// <summary>
/// Produces the final user-facing answer once every task in the DAG has completed.
/// Spec §4.3: "Lord Helm synthesizes the final output for the user."
/// </summary>
public interface ISynthesizer
{
    Task<string> SynthesizeAsync(SynthesisRequest req, CancellationToken ct = default);
}

public sealed class LlmSynthesizer : ISynthesizer
{
    private readonly IProviderOrchestrator _providers;
    private readonly IExpertRegistry _experts;
    private readonly ILogger<LlmSynthesizer> _logger;

    public LlmSynthesizer(IProviderOrchestrator providers, IExpertRegistry experts, ILogger<LlmSynthesizer> logger)
    {
        _providers = providers;
        _experts = experts;
        _logger = logger;
    }

    public async Task<string> SynthesizeAsync(SynthesisRequest req, CancellationToken ct = default)
    {
        if (req.NodeOutputs.Count == 0)
            return "(no task outputs to synthesize)";

        if (req.NodeOutputs.Count == 1 && req.Dag.Count == 1)
            return req.NodeOutputs.First().Value; // nothing to synthesize; pass through

        var prompt = BuildPrompt(req);
        var contextEstimate = Math.Max(4000, prompt.Length / 4);

        // Prefer the dedicated "synthesiser" expert so the act is mirrored to
        // its engram namespace (retrospectives + reflection can query it) and
        // the call is subject to policy/budget/approval like any other expert.
        // Falls back to the provider orchestrator when the persona isn't
        // registered — keeps tests that don't set up the expert registry green.
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
            preferredVendor: "claude",
            modelOverride: null,
            prompt: prompt,
            hint: new ProviderTaskHint(TaskKind: "summarisation",
                EstimatedContextTokens: contextEstimate),
            maxTokens: 1024,
            temperature: 0.1f,
            ct: ct);

        if (response.Error is not null || string.IsNullOrWhiteSpace(response.AssistantMessage))
        {
            _logger.LogWarning("Synthesis failed ({Err}); falling back to concatenation.",
                response.Error?.Message ?? "empty response");
            return FallbackConcat(req);
        }
        return response.AssistantMessage.Trim();
    }

    public static string BuildPrompt(SynthesisRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are Lord Helm, the orchestrator. You are producing the final user-facing answer.");
        sb.AppendLine();
        sb.AppendLine($"ORIGINAL GOAL: {req.Goal}");
        sb.AppendLine();
        sb.AppendLine("SUB-TASK OUTPUTS (in DAG order):");
        foreach (var node in req.Dag)
        {
            var output = req.NodeOutputs.TryGetValue(node.Id, out var o) ? o : "(missing)";
            sb.AppendLine($"### {node.Id} -- {node.Goal}");
            if (node.Persona is not null) sb.AppendLine($"*persona: {node.Persona}*");
            sb.AppendLine(output.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("Synthesize a single user-facing response that:");
        sb.AppendLine("- directly answers the ORIGINAL GOAL.");
        sb.AppendLine("- preserves every concrete citation (file:line, ids, numbers).");
        sb.AppendLine("- flags explicit disagreements between sub-tasks if any.");
        sb.AppendLine("- is concise — the user wants the answer, not a retelling of the work.");
        sb.AppendLine();
        sb.AppendLine("Output Markdown. Do not preface with 'here is' or similar filler.");
        return sb.ToString();
    }

    public static string FallbackConcat(SynthesisRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {req.Goal}");
        sb.AppendLine();
        foreach (var node in req.Dag)
        {
            var output = req.NodeOutputs.TryGetValue(node.Id, out var o) ? o : "(missing)";
            sb.AppendLine($"## {node.Goal}");
            sb.AppendLine(output.Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
