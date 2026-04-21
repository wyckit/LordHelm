namespace LordHelm.Orchestrator;

public sealed record TaskNode(string Id, string Goal, IReadOnlyList<string> DependsOn);

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
}
