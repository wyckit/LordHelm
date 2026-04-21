namespace LordHelm.Orchestrator;

public sealed record SwarmMemberOutput(string MemberId, string Persona, string Vendor, string Output);

/// <summary>
/// Merge N parallel expert outputs (from a SwarmSize &gt; 1 task) into a single string.
/// Implementations range from trivial concatenation to LLM-driven synthesis. The
/// aggregator is called only for tasks with <see cref="TaskNode.SwarmSize"/> &gt; 1.
/// </summary>
public interface ISwarmAggregator
{
    Task<string> AggregateAsync(TaskNode task, IReadOnlyList<SwarmMemberOutput> outputs, CancellationToken ct = default);
}

/// <summary>
/// Cheap default: stitch outputs with a labeled header per member. No LLM cost.
/// Good enough when the downstream consumer is another expert that can sift through
/// structured input.
/// </summary>
public sealed class ConcatSwarmAggregator : ISwarmAggregator
{
    public Task<string> AggregateAsync(TaskNode task, IReadOnlyList<SwarmMemberOutput> outputs, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Swarm output for: {task.Goal}");
        sb.AppendLine($"# {outputs.Count} expert(s) contributed");
        sb.AppendLine();
        foreach (var o in outputs)
        {
            sb.AppendLine($"## {o.Persona} ({o.Vendor}) :: {o.MemberId}");
            sb.AppendLine(o.Output.Trim());
            sb.AppendLine();
        }
        return Task.FromResult(sb.ToString());
    }
}
