namespace LordHelm.Orchestrator.Topology;

public enum TopologyKind { Helm, Orc, Agent }

public enum AgentType { Code, Research, Data, Ops, Write, Design }

public enum AgentRunState { Working, Waiting, Blocked, Idle, Done }

public sealed record TopologyNode(
    string Id,
    string Label,
    TopologyKind Kind,
    double X,           // 0-100 percent
    double Y,
    AgentType? AgentType = null,
    AgentRunState Status = AgentRunState.Idle,
    double Load = 0.0,  // 0-1, drives node size + edge styling
    int Tokens = 0,
    string? Task = null);

public enum EdgeKind
{
    /// <summary>Org-chart relationship (helm → pod → agent). Always rendered faint.</summary>
    Hierarchy,
    /// <summary>Observed data flow between two nodes (agent_A's output reached agent_B).
    /// Staleness fades the stroke: fresh → bright accent, >2min → faded, >10min → dropped.</summary>
    Dataflow,
}

public sealed record TopologyEdge(
    string From,
    string To,
    EdgeKind Kind = EdgeKind.Hierarchy,
    DateTimeOffset? ObservedAt = null);

/// <summary>
/// Snapshot + live mutations for the dashboard topology. Seeded with the
/// fleet layout from the design handoff (helm + 4 pod orchestrators + 12
/// agents). Real load % comes from <see cref="WidgetState"/>: running
/// widgets count toward agent load; status bucket counts roll up into pod
/// and helm loads. A background hosted service refreshes on every
/// <see cref="WidgetState.OnChanged"/> tick so the topology mirrors the
/// live dashboard without a polling loop.
/// </summary>
public sealed class TopologyState
{
    private readonly object _gate = new();
    private Dictionary<string, TopologyNode> _nodes;
    private IReadOnlyList<TopologyEdge> _edges;

    public event Action? OnChanged;
    public event Action? OnSelectionChanged;

    public string? SelectedAgentId { get; private set; }

    public void Select(string? id)
    {
        if (SelectedAgentId == id) return;
        SelectedAgentId = id;
        OnSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Construct empty so <see cref="LordHelm.Orchestrator.Topology.TopologyProjectionService"/>
    /// fills in the live graph from the real registries on startup. Pass
    /// <paramref name="seedDemo"/>=true only for preview/demo screens.
    /// </summary>
    public TopologyState(bool seedDemo = false)
    {
        if (seedDemo)
        {
            _nodes = SeedNodes().ToDictionary(n => n.Id);
            _edges = SeedEdges();
        }
        else
        {
            _nodes = new Dictionary<string, TopologyNode>();
            _edges = Array.Empty<TopologyEdge>();
        }
    }

    public IReadOnlyList<TopologyNode> Nodes
    {
        get { lock (_gate) return _nodes.Values.ToList(); }
    }

    public IReadOnlyList<TopologyEdge> Edges => _edges;

    public void UpdateLoad(string nodeId, double load, int tokens = 0, AgentRunState? status = null, string? task = null)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(nodeId, out var n)) return;
            _nodes[nodeId] = n with
            {
                Load = Math.Clamp(load, 0.0, 1.0),
                Tokens = tokens > 0 ? tokens : n.Tokens,
                Status = status ?? n.Status,
                Task = task ?? n.Task,
            };
        }
        OnChanged?.Invoke();
    }

    public void ReplaceAll(IEnumerable<TopologyNode> nodes, IEnumerable<TopologyEdge> edges)
    {
        lock (_gate)
        {
            _nodes = nodes.ToDictionary(n => n.Id);
            _edges = edges.ToList();
        }
        OnChanged?.Invoke();
    }

    // Design-handoff-parity seed. Hand-placed positions produce the 'fleet
    // around central helm' layout with 4 pod orchestrators at cardinal
    // positions and 12 agents around the perimeter.
    private static IReadOnlyList<TopologyNode> SeedNodes() => new[]
    {
        new TopologyNode("helm",  "Lord Helm",  TopologyKind.Helm, 50, 50, Load: 0.5),

        new TopologyNode("orc-w", "Write Pod",  TopologyKind.Orc,  22, 28, Load: 0.3),
        new TopologyNode("orc-c", "Code Pod",   TopologyKind.Orc,  78, 28, Load: 0.4),
        new TopologyNode("orc-d", "Data Pod",   TopologyKind.Orc,  22, 72, Load: 0.3),
        new TopologyNode("orc-o", "Ops Pod",    TopologyKind.Orc,  78, 72, Load: 0.2),

        new TopologyNode("a01", "Scribe-7",    TopologyKind.Agent, 12, 14, AgentType.Write,    AgentRunState.Working, 0.62),
        new TopologyNode("a08", "Vellum",      TopologyKind.Agent, 30, 12, AgentType.Write,    AgentRunState.Idle,    0.08),
        new TopologyNode("a07", "Loom",        TopologyKind.Agent, 42, 18, AgentType.Design,   AgentRunState.Working, 0.55),

        new TopologyNode("a11", "Anvil",       TopologyKind.Agent, 70, 12, AgentType.Code,     AgentRunState.Working, 0.71),
        new TopologyNode("a03", "Forgemaster", TopologyKind.Agent, 88, 14, AgentType.Code,     AgentRunState.Working, 0.78),

        new TopologyNode("a02", "Cartographer",TopologyKind.Agent, 88, 42, AgentType.Research, AgentRunState.Working, 0.41),
        new TopologyNode("a12", "Compass",     TopologyKind.Agent, 60, 42, AgentType.Research, AgentRunState.Blocked, 0.35),
        new TopologyNode("a10", "Beacon",      TopologyKind.Agent, 92, 60, AgentType.Research, AgentRunState.Waiting, 0.18),

        new TopologyNode("a05", "Oracle",      TopologyKind.Agent,  8, 62, AgentType.Data,     AgentRunState.Working, 0.88),
        new TopologyNode("a09", "Ledger",      TopologyKind.Agent, 12, 84, AgentType.Data,     AgentRunState.Working, 0.19),

        new TopologyNode("a06", "Sentinel",    TopologyKind.Agent, 68, 84, AgentType.Ops,      AgentRunState.Blocked, 0.35),
        new TopologyNode("a04", "Herald",      TopologyKind.Agent, 88, 86, AgentType.Ops,      AgentRunState.Waiting, 0.18),
    };

    private static IReadOnlyList<TopologyEdge> SeedEdges() => new[]
    {
        new TopologyEdge("helm", "orc-w"), new TopologyEdge("helm", "orc-c"),
        new TopologyEdge("helm", "orc-d"), new TopologyEdge("helm", "orc-o"),

        new TopologyEdge("orc-w", "a01"), new TopologyEdge("orc-w", "a08"), new TopologyEdge("orc-w", "a07"),
        new TopologyEdge("orc-c", "a03"), new TopologyEdge("orc-c", "a11"),
        new TopologyEdge("orc-c", "a02"), new TopologyEdge("orc-c", "a10"), new TopologyEdge("orc-c", "a12"),
        new TopologyEdge("orc-d", "a05"), new TopologyEdge("orc-d", "a09"),
        new TopologyEdge("orc-o", "a04"), new TopologyEdge("orc-o", "a06"),
    };
}
