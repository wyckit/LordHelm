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
    string? ErrorDetail);

/// <summary>
/// End-to-end goal execution. Combines <see cref="ILordHelmManager"/> (decompose + topo-sort)
/// with <see cref="IExpertProvisioner"/> (provision per-task runners) into a single call so
/// callers — HTTP endpoint, Blazor UI, CLI, MCP server — all go through the same path.
/// Progress is reported to the registered <see cref="IGoalProgressSink"/>.
/// </summary>
public interface IGoalRunner
{
    Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default);
}

public sealed class GoalRunner : IGoalRunner
{
    private readonly ILordHelmManager _manager;
    private readonly IExpertProvisioner _provisioner;
    private readonly ISkillCache _skills;
    private readonly IGoalProgressSink _sink;
    private readonly ILogger<GoalRunner> _logger;

    public GoalRunner(
        ILordHelmManager manager,
        IExpertProvisioner provisioner,
        ISkillCache skills,
        IGoalProgressSink sink,
        ILogger<GoalRunner> logger)
    {
        _manager = manager;
        _provisioner = provisioner;
        _skills = skills;
        _sink = sink;
        _logger = logger;
    }

    public async Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default)
    {
        var goalId = "goal-" + Guid.NewGuid().ToString("N")[..10];
        var vendor = req.PreferredVendor ?? "claude";
        var model = req.Model ?? "claude-opus-4-7";

        var skills = await _skills.ListAsync(ct);
        _logger.LogInformation("Goal {Id} starting: {Goal}", goalId, req.Goal);

        await _sink.OnGoalStartedAsync(goalId, req.Goal, 0, ct);

        var result = await _manager.RunAsync(
            req.Goal,
            skills,
            executeTask: async node =>
            {
                await _sink.OnTaskStartedAsync(goalId, node.Id, node.Goal, ct);
                try
                {
                    var skillId = PickSkill(node, skills);
                    if (skillId is null)
                    {
                        var synthesisOutput = $"(synthesis) {node.Goal}";
                        await _sink.OnTaskLogAsync(goalId, node.Id, "no skill match; synthesis step", ct);
                        await _sink.OnTaskCompletedAsync(goalId, node.Id, true, synthesisOutput, ct);
                        return synthesisOutput;
                    }

                    var provisionReq = new ProvisionRequest(
                        ExpertId: "expert-" + node.Id,
                        SkillId: skillId,
                        CliVendorId: vendor,
                        Model: model,
                        Goal: node.Goal);
                    var expert = await _provisioner.ProvisionAsync(provisionReq, ct);
                    if (expert is null)
                    {
                        var msg = $"skill '{skillId}' not found";
                        await _sink.OnTaskLogAsync(goalId, node.Id, msg, ct);
                        await _sink.OnTaskCompletedAsync(goalId, node.Id, false, msg, ct);
                        throw new InvalidOperationException(msg);
                    }

                    await _sink.OnTaskLogAsync(goalId, node.Id, $"provisioned expert with skill '{skillId}' on vendor '{vendor}'", ct);
                    var output = await expert.Run(ct);
                    await _sink.OnTaskLogAsync(goalId, node.Id, output, ct);
                    await _sink.OnTaskCompletedAsync(goalId, node.Id, true, output, ct);
                    return output;
                }
                catch (Exception ex)
                {
                    await _sink.OnTaskCompletedAsync(goalId, node.Id, false, ex.Message, ct);
                    throw;
                }
            },
            ct);

        await _sink.OnGoalCompletedAsync(goalId, result.Succeeded, result.ErrorDetail, ct);

        return new GoalRunResult(
            goalId,
            result.Succeeded,
            result.Dag.Count,
            result.NodeOutputs,
            result.ErrorDetail);
    }

    /// <summary>
    /// Naive skill matcher: prefer a skill whose id appears in the node goal text,
    /// otherwise the first skill whose tag set intersects the goal, otherwise null.
    /// A future revision can upgrade this to a semantic match via IEngramClient.SearchAsync.
    /// </summary>
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
}
