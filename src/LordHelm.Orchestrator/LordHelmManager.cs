using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record ManagerResult(
    string Goal,
    IReadOnlyList<TaskNode> Dag,
    IReadOnlyDictionary<string, string> NodeOutputs,
    bool Succeeded,
    string? ErrorDetail);

public interface ILordHelmManager
{
    Task<ManagerResult> RunAsync(string goal, IReadOnlyList<SkillManifest> availableSkills, Func<TaskNode, Task<string>> executeTask, CancellationToken ct = default);
}

public sealed class LordHelmManager : ILordHelmManager
{
    private readonly IGoalDecomposer _decomposer;
    private readonly ILogger<LordHelmManager> _logger;

    public LordHelmManager(IGoalDecomposer decomposer, ILogger<LordHelmManager> logger)
    {
        _decomposer = decomposer;
        _logger = logger;
    }

    public async Task<ManagerResult> RunAsync(string goal, IReadOnlyList<SkillManifest> availableSkills, Func<TaskNode, Task<string>> executeTask, CancellationToken ct = default)
    {
        var nodes = await _decomposer.DecomposeAsync(goal, availableSkills, ct);
        IReadOnlyList<TaskNode> sorted;
        try { sorted = TaskDag.TopoSort(nodes); }
        catch (Exception ex) { return new ManagerResult(goal, nodes, new Dictionary<string, string>(), false, ex.Message); }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in sorted)
        {
            if (ct.IsCancellationRequested) return new ManagerResult(goal, sorted, outputs, false, "cancelled");
            try
            {
                var output = await executeTask(node);
                outputs[node.Id] = output;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task {Node} failed", node.Id);
                return new ManagerResult(goal, sorted, outputs, false, $"{node.Id}: {ex.Message}");
            }
        }

        return new ManagerResult(goal, sorted, outputs, true, null);
    }
}
