using LordHelm.Core;
using LordHelm.Skills;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record GoalRunRequest(
    string Goal,
    string? PreferredVendor = null,
    string? Model = null,
    int Priority = 0);

public sealed record GoalRunResult(
    string GoalId,
    bool Succeeded,
    int DagNodeCount,
    IReadOnlyDictionary<string, string> NodeOutputs,
    string? Synthesis,
    string? ErrorDetail);

/// <summary>
/// End-to-end goal execution. Combines Manager + Provisioner + Synthesiser into a
/// single call so every caller (HTTP, UI, CLI, MCP) goes through the same path.
/// Emits progress to the registered <see cref="IGoalProgressSink"/>.
/// </summary>
public interface IGoalRunner
{
    Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default);
}

public sealed class GoalRunner : IGoalRunner
{
    private static readonly string[] _vendorRotation = { "claude", "gemini", "codex" };

    private readonly ILordHelmManager _manager;
    private readonly IExpertProvisioner _provisioner;
    private readonly ISkillCache _skills;
    private readonly ISynthesizer _synthesizer;
    private readonly IGoalProgressSink _sink;
    private readonly ILogger<GoalRunner> _logger;

    public GoalRunner(
        ILordHelmManager manager,
        IExpertProvisioner provisioner,
        ISkillCache skills,
        ISynthesizer synthesizer,
        IGoalProgressSink sink,
        ILogger<GoalRunner> logger)
    {
        _manager = manager;
        _provisioner = provisioner;
        _skills = skills;
        _synthesizer = synthesizer;
        _sink = sink;
        _logger = logger;
    }

    public async Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default)
    {
        var goalId = "goal-" + Guid.NewGuid().ToString("N")[..10];
        var defaultVendor = req.PreferredVendor ?? "claude";
        var defaultModel = req.Model ?? "claude-opus-4-7";

        var skills = await _skills.ListAsync(ct);
        _logger.LogInformation("Goal {Id} starting: {Goal}", goalId, req.Goal);

        await _sink.OnGoalStartedAsync(goalId, req.Goal, 0, ct);

        var managerResult = await _manager.RunAsync(
            req.Goal,
            skills,
            executeTask: async (node, memberIndex, innerCt) =>
            {
                var vendor = node.PreferredVendor
                    ?? (node.SwarmSize > 1 && node.SwarmStrategy == SwarmStrategy.Diverse
                        ? _vendorRotation[memberIndex % _vendorRotation.Length]
                        : defaultVendor);
                var memberTaskId = node.SwarmSize > 1 ? $"{node.Id}#m{memberIndex}" : node.Id;
                var memberLabel = node.SwarmSize > 1
                    ? $"{node.Goal} [{node.Persona ?? "expert"} via {vendor} #m{memberIndex}]"
                    : node.Goal;

                await _sink.OnTaskStartedAsync(goalId, memberTaskId, memberLabel, innerCt);
                try
                {
                    var skillId = node.Skill ?? PickSkill(node, skills);
                    if (skillId is null)
                    {
                        var synthesisOutput = $"(synthesis) {node.Goal}";
                        await _sink.OnTaskLogAsync(goalId, memberTaskId, "no skill match; synthesis step", innerCt);
                        await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, synthesisOutput, innerCt);
                        return synthesisOutput;
                    }

                    var provReq = new ProvisionRequest(
                        ExpertId: $"expert-{memberTaskId}",
                        SkillId: skillId,
                        CliVendorId: vendor,
                        Model: defaultModel,
                        Goal: node.Goal,
                        Persona: node.Persona);
                    var expert = await _provisioner.ProvisionAsync(provReq, innerCt);
                    if (expert is null)
                    {
                        var msg = $"skill '{skillId}' not found";
                        await _sink.OnTaskLogAsync(goalId, memberTaskId, msg, innerCt);
                        await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, msg, innerCt);
                        throw new InvalidOperationException(msg);
                    }

                    await _sink.OnTaskLogAsync(goalId, memberTaskId,
                        $"provisioned {expert.Profile.ExpertId} with skill '{skillId}' on {vendor}", innerCt);
                    var output = await expert.Run(innerCt);
                    await _sink.OnTaskLogAsync(goalId, memberTaskId, output, innerCt);
                    await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, output, innerCt);
                    return output;
                }
                catch (Exception ex)
                {
                    await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, ex.Message, innerCt);
                    throw;
                }
            },
            ct);

        string? synthesis = null;
        if (managerResult.Succeeded)
        {
            try
            {
                synthesis = await _synthesizer.SynthesizeAsync(
                    new SynthesisRequest(req.Goal, managerResult.Dag, managerResult.NodeOutputs),
                    ct);
                await _sink.OnTaskLogAsync(goalId, "goal", "synthesis: " + Truncate(synthesis, 200), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Synthesis failed for goal {Id}", goalId);
            }
        }

        await _sink.OnGoalCompletedAsync(goalId, managerResult.Succeeded,
            managerResult.ErrorDetail ?? synthesis, ct);

        return new GoalRunResult(
            goalId,
            managerResult.Succeeded,
            managerResult.Dag.Count,
            managerResult.NodeOutputs,
            synthesis,
            managerResult.ErrorDetail);
    }

    public static string? PickSkill(TaskNode node, IReadOnlyList<SkillManifest> skills)
    {
        if (skills.Count == 0) return null;
        var goalLower = node.Goal.ToLowerInvariant();
        foreach (var s in skills)
        {
            if (goalLower.Contains(s.Id.ToLowerInvariant())) return s.Id;
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
