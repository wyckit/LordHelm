namespace LordHelm.Orchestrator;

public enum SwarmStrategy
{
    /// <summary>Single expert handles the task.</summary>
    Single,
    /// <summary>N independent experts run the same task; outputs are merged by the aggregator.</summary>
    Redundant,
    /// <summary>N experts run with diverse vendors / personas; outputs are merged.</summary>
    Diverse,
}

public sealed record TaskNode(
    string Id,
    string Goal,
    IReadOnlyList<string> DependsOn,
    string? Persona = null,
    string? Skill = null,
    string? PreferredVendor = null,
    int SwarmSize = 1,
    SwarmStrategy SwarmStrategy = SwarmStrategy.Single);

public static class TaskDag
{
    /// <summary>
    /// Topological sort via Kahn's algorithm. Throws InvalidOperationException if the graph
    /// contains a cycle (detected when the frontier empties before every node is emitted).
    /// </summary>
    public static IReadOnlyList<TaskNode> TopoSort(IEnumerable<TaskNode> nodes)
    {
        var list = nodes.ToList();
        var byId = list.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var inDegree = list.ToDictionary(n => n.Id, n => n.DependsOn.Count, StringComparer.Ordinal);
        var ready = new Queue<string>(list.Where(n => n.DependsOn.Count == 0).Select(n => n.Id));
        var result = new List<TaskNode>();

        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            result.Add(byId[id]);
            foreach (var other in list.Where(n => n.DependsOn.Contains(id)))
            {
                if (--inDegree[other.Id] == 0) ready.Enqueue(other.Id);
            }
        }
        if (result.Count != list.Count)
            throw new InvalidOperationException("Cycle detected in task DAG.");
        return result;
    }

    /// <summary>
    /// Group the DAG into dependency waves. Every node in wave N has zero unsatisfied
    /// dependencies once all nodes in waves 0..N-1 complete. Nodes within a wave are
    /// independent and can run in parallel — the spec's "experts work in parallel,
    /// downstream triggers when prereqs populate" is realised by running each wave
    /// under <c>Task.WhenAll</c>. Throws on cycles.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TaskNode>> ComputeWaves(IEnumerable<TaskNode> nodes)
    {
        var list = nodes.ToList();
        var byId = list.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var remaining = new Dictionary<string, int>(
            list.ToDictionary(n => n.Id, n => n.DependsOn.Count, StringComparer.Ordinal),
            StringComparer.Ordinal);

        var waves = new List<IReadOnlyList<TaskNode>>();
        while (remaining.Count > 0)
        {
            var wave = remaining.Where(kv => kv.Value == 0).Select(kv => byId[kv.Key]).ToList();
            if (wave.Count == 0)
                throw new InvalidOperationException("Cycle detected in task DAG (wave computation).");
            waves.Add(wave);

            foreach (var completed in wave) remaining.Remove(completed.Id);
            foreach (var completed in wave)
            {
                foreach (var other in list)
                {
                    if (remaining.ContainsKey(other.Id) && other.DependsOn.Contains(completed.Id))
                        remaining[other.Id]--;
                }
            }
        }
        return waves;
    }
}
