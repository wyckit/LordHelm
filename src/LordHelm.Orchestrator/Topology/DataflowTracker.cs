using System.Collections.Concurrent;

namespace LordHelm.Orchestrator.Topology;

public sealed record DataflowObservation(
    string From,
    string To,
    DateTimeOffset ObservedAt,
    string? Reason = null);

/// <summary>
/// In-memory dataflow ledger. Any subsystem that observes agent_A feeding
/// agent_B's namespace (artifact mirror / engram cross-link / DAG edge
/// completion) records the flow here. The <see cref="TopologyProjectionService"/>
/// reads recent observations and emits them as <c>EdgeKind.Dataflow</c> edges
/// with <c>ObservedAt</c> stamps; stale flows are auto-pruned past the
/// render TTL.
///
/// Panel-endorsed shape: the ledger is append-only; identical (from, to)
/// observations update the timestamp rather than duplicate — keeps the
/// render stable and puts refreshing flows at the top of the staleness
/// gradient automatically.
/// </summary>
public sealed class DataflowTracker
{
    private readonly ConcurrentDictionary<(string, string), DataflowObservation> _edges =
        new();
    private static readonly TimeSpan RenderTtl = TimeSpan.FromMinutes(10);

    public event Action? OnChanged;

    public void Observe(string from, string to, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ||
            string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return;

        _edges[(from, to)] = new DataflowObservation(
            From: from, To: to,
            ObservedAt: DateTimeOffset.UtcNow,
            Reason: reason);
        OnChanged?.Invoke();
    }

    public IReadOnlyList<DataflowObservation> Recent()
    {
        var cutoff = DateTimeOffset.UtcNow - RenderTtl;
        var live = new List<DataflowObservation>();
        foreach (var kv in _edges)
        {
            if (kv.Value.ObservedAt >= cutoff) live.Add(kv.Value);
            else _edges.TryRemove(kv.Key, out _);
        }
        return live;
    }

    public void Clear() { _edges.Clear(); OnChanged?.Invoke(); }
}
