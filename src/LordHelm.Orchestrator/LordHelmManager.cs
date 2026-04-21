using System.Collections.Concurrent;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record ManagerResult(
    string Goal,
    IReadOnlyList<TaskNode> Dag,
    IReadOnlyDictionary<string, string> NodeOutputs,
    bool Succeeded,
    string? ErrorDetail);

/// <summary>
/// Executor signature. Given a task node AND an optional swarm-member index, produce
/// the output for that member. For tasks with SwarmSize == 1 the memberIndex is 0.
/// For swarm tasks the manager calls the executor N times with memberIndex 0..N-1
/// concurrently, then feeds the outputs through <see cref="ISwarmAggregator"/>.
/// </summary>
public delegate Task<string> TaskExecutor(TaskNode node, int memberIndex, CancellationToken ct);

public interface ILordHelmManager
{
    Task<ManagerResult> RunAsync(
        string goal,
        IReadOnlyList<SkillManifest> availableSkills,
        TaskExecutor executeTask,
        CancellationToken ct = default);
}

/// <summary>
/// The Think Tank orchestrator (spec §4). Flow:
/// 1. Decomposition -- <see cref="IGoalDecomposer"/> breaks the goal into a TaskNode DAG.
/// 2. Dynamic Assembly -- the caller provides a <see cref="TaskExecutor"/> that provisions
///    a specialised Expert per task + swarm-member index.
/// 3. Execution + Synthesis -- dependency waves run in parallel via Task.WhenAll;
///    each wave must complete before the next can start. For any task with
///    SwarmSize &gt; 1 the manager fans out memberIndex 0..N-1 concurrently and feeds the
///    results through <see cref="ISwarmAggregator"/> before the task output is recorded.
/// </summary>
public sealed class LordHelmManager : ILordHelmManager
{
    private readonly IGoalDecomposer _decomposer;
    private readonly ISwarmAggregator _swarmAggregator;
    private readonly ILogger<LordHelmManager> _logger;

    public LordHelmManager(
        IGoalDecomposer decomposer,
        ISwarmAggregator swarmAggregator,
        ILogger<LordHelmManager> logger)
    {
        _decomposer = decomposer;
        _swarmAggregator = swarmAggregator;
        _logger = logger;
    }

    public async Task<ManagerResult> RunAsync(
        string goal,
        IReadOnlyList<SkillManifest> availableSkills,
        TaskExecutor executeTask,
        CancellationToken ct = default)
    {
        var nodes = await _decomposer.DecomposeAsync(goal, availableSkills, ct);

        IReadOnlyList<IReadOnlyList<TaskNode>> waves;
        try { waves = TaskDag.ComputeWaves(nodes); }
        catch (Exception ex)
        {
            return new ManagerResult(goal, nodes, new Dictionary<string, string>(), false, ex.Message);
        }

        var outputs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _logger.LogInformation("Think Tank: {Count} task(s) across {Waves} wave(s)",
            nodes.Count, waves.Count);

        for (int w = 0; w < waves.Count; w++)
        {
            var wave = waves[w];
            if (ct.IsCancellationRequested)
            {
                return new ManagerResult(goal, nodes, new Dictionary<string, string>(outputs, StringComparer.Ordinal), false, "cancelled");
            }

            _logger.LogInformation("Wave {W}: {N} task(s) running in parallel", w, wave.Count);

            var waveTasks = wave.Select(node => RunOneAsync(node, executeTask, outputs, ct));
            try
            {
                await Task.WhenAll(waveTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wave {W} failed", w);
                return new ManagerResult(goal, nodes,
                    new Dictionary<string, string>(outputs, StringComparer.Ordinal),
                    false, ex.Message);
            }
        }

        return new ManagerResult(goal, nodes,
            new Dictionary<string, string>(outputs, StringComparer.Ordinal),
            true, null);
    }

    private async Task RunOneAsync(
        TaskNode node,
        TaskExecutor executeTask,
        ConcurrentDictionary<string, string> outputs,
        CancellationToken ct)
    {
        var size = Math.Max(1, node.SwarmSize);
        if (size == 1)
        {
            var output = await executeTask(node, 0, ct);
            outputs[node.Id] = output;
            return;
        }

        // Swarm: N parallel executors, aggregate the results.
        var memberTasks = Enumerable.Range(0, size)
            .Select(i => RunMemberAsync(node, i, executeTask, ct))
            .ToArray();
        var members = await Task.WhenAll(memberTasks);
        var aggregated = await _swarmAggregator.AggregateAsync(node, members, ct);
        outputs[node.Id] = aggregated;
    }

    private static async Task<SwarmMemberOutput> RunMemberAsync(
        TaskNode node,
        int memberIndex,
        TaskExecutor executeTask,
        CancellationToken ct)
    {
        var output = await executeTask(node, memberIndex, ct);
        return new SwarmMemberOutput(
            MemberId: $"{node.Id}#m{memberIndex}",
            Persona: node.Persona ?? "default",
            Vendor: node.PreferredVendor ?? "claude",
            Output: output);
    }
}
