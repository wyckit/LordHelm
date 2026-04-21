using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

/// <summary>
/// Engram-driven execution mode. Instead of computing dependency waves up front,
/// every task subscribes to its prerequisite engram nodes via <see cref="IDataflowBus"/>;
/// a task fires when ALL its prereq nodes have been published. Publications are
/// idempotent — a guard record keyed by (taskId, inputNodeHash) short-circuits
/// re-execution if the same inputs arrive twice.
///
/// This matches the spec's "downstream Experts trigger when prerequisite Engram
/// nodes are populated" pattern literally. The wave-parallel <see cref="LordHelmManager"/>
/// remains the default; select this one via DI when the orchestration benefits
/// from event-driven fan-out (e.g. some tasks arrive late from external agents).
/// </summary>
public sealed class EngramDrivenManager : ILordHelmManager
{
    private readonly IGoalDecomposer _decomposer;
    private readonly IEngramClient _engram;
    private readonly IDataflowBus _bus;
    private readonly ISwarmAggregator _swarmAggregator;
    private readonly ILogger<EngramDrivenManager> _logger;

    public EngramDrivenManager(
        IGoalDecomposer decomposer,
        IEngramClient engram,
        IDataflowBus bus,
        ISwarmAggregator swarmAggregator,
        ILogger<EngramDrivenManager> logger)
    {
        _decomposer = decomposer;
        _engram = engram;
        _bus = bus;
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
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Detect cycles up front via ComputeWaves — event-driven mode still
        // rejects cyclic DAGs (the engram can't populate a node depending on itself).
        try { _ = TaskDag.ComputeWaves(nodes); }
        catch (Exception ex)
        {
            return new ManagerResult(goal, nodes, new Dictionary<string, string>(), false, ex.Message);
        }

        var outputs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var completions = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        foreach (var n in nodes) completions[n.Id] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runId = "run-" + Guid.NewGuid().ToString("N")[..10];
        var taskNamespace = $"lord_helm_task_outputs/{runId}";

        _logger.LogInformation("EngramDrivenManager: {N} nodes under run {RunId}", nodes.Count, runId);

        // Subscribe every downstream node to its prerequisites via the bus.
        var subs = new List<string>();
        foreach (var node in nodes)
        {
            if (node.DependsOn.Count == 0) continue;
            var subId = $"{runId}/{node.Id}/waiter";
            subs.Add(subId);
            await _bus.SubscribeAsync(
                new SubscriptionSpec(subId, taskNamespace, "*"),
                async ev =>
                {
                    var readyPrereqs = node.DependsOn.All(d => completions[d].Task.IsCompletedSuccessfully);
                    if (!readyPrereqs) return;
                    // Fire-and-forget so the subscription handler doesn't block the bus.
                    _ = RunAndPublishAsync(node, taskNamespace, runId, executeTask, outputs, completions, byId, ct);
                }, ct);
        }

        // Kick off every root (no prereqs) immediately.
        var rootFires = nodes
            .Where(n => n.DependsOn.Count == 0)
            .Select(n => RunAndPublishAsync(n, taskNamespace, runId, executeTask, outputs, completions, byId, ct));
        try
        {
            await Task.WhenAll(rootFires);
            await Task.WhenAll(completions.Values.Select(tcs => tcs.Task));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engram-driven run {RunId} failed", runId);
            foreach (var id in subs) await _bus.UnsubscribeAsync(id);
            return new ManagerResult(goal, nodes, new Dictionary<string, string>(outputs, StringComparer.Ordinal), false, ex.Message);
        }

        foreach (var id in subs) await _bus.UnsubscribeAsync(id);

        return new ManagerResult(goal, nodes,
            new Dictionary<string, string>(outputs, StringComparer.Ordinal), true, null);
    }

    private async Task RunAndPublishAsync(
        TaskNode node,
        string taskNamespace,
        string runId,
        TaskExecutor executeTask,
        ConcurrentDictionary<string, string> outputs,
        ConcurrentDictionary<string, TaskCompletionSource<bool>> completions,
        IReadOnlyDictionary<string, TaskNode> byId,
        CancellationToken ct)
    {
        if (outputs.ContainsKey(node.Id)) return; // already ran (idempotency guard)

        var inputHash = ComputeInputHash(node, outputs, byId);
        var guardId = $"{node.Id}.guard.{inputHash}";
        var existing = await _engram.GetAsync(taskNamespace, guardId, ct);
        if (existing is not null)
        {
            outputs[node.Id] = existing.Text;
            completions[node.Id].TrySetResult(true);
            return;
        }

        string output;
        try
        {
            var size = Math.Max(1, node.SwarmSize);
            if (size == 1)
            {
                output = await executeTask(node, 0, ct);
            }
            else
            {
                var members = await Task.WhenAll(Enumerable.Range(0, size).Select(async i =>
                    new SwarmMemberOutput(
                        $"{node.Id}#m{i}",
                        node.Persona ?? "default",
                        node.PreferredVendor ?? "claude",
                        await executeTask(node, i, ct))));
                output = await _swarmAggregator.AggregateAsync(node, members, ct);
            }
        }
        catch (Exception ex)
        {
            completions[node.Id].TrySetException(ex);
            throw;
        }

        outputs[node.Id] = output;
        await _engram.StoreAsync(taskNamespace, guardId, output,
            category: "task-output",
            metadata: new Dictionary<string, string>
            {
                ["run_id"] = runId,
                ["task_id"] = node.Id,
                ["input_hash"] = inputHash,
            },
            ct);
        await _bus.PublishAsync(new NodeEvent(
            new NodeRef(taskNamespace, guardId, new Dictionary<string, string> { ["task_id"] = node.Id }),
            output, DateTimeOffset.UtcNow), ct);
        completions[node.Id].TrySetResult(true);
    }

    internal static string ComputeInputHash(
        TaskNode node,
        ConcurrentDictionary<string, string> outputs,
        IReadOnlyDictionary<string, TaskNode> byId)
    {
        var sb = new StringBuilder();
        sb.Append(node.Id).Append('|').Append(node.Goal);
        foreach (var dep in node.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
        {
            sb.Append('|').Append(dep).Append('=');
            sb.Append(outputs.TryGetValue(dep, out var v) ? v : "");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }
}
