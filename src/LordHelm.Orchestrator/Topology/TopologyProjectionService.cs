using LordHelm.Orchestrator.Overseers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Topology;

/// <summary>
/// Live projection of Lord Helm's real runtime into <see cref="TopologyState"/>.
/// Subscribes to every source of truth (IExpertRegistry, OverseerRegistry,
/// IFleetTaskSource) and, on any change event, debounces for 300 ms then
/// recomputes the whole graph.
///
/// The result is deterministic and panel-endorsed:
///  • nodes = helm (center) + synthesiser (helm-adjacent, cross-cutting) +
///    one pod per <see cref="AgentType"/> that has ≥1 expert + one node per
///    registered expert + one node per registered overseer (rendered under
///    a dedicated "overseers" pod);
///  • edges = helm → every pod (hierarchy) + every pod → every member
///    (hierarchy). Dataflow edges come in a later slice.
///  • agent load = aggregate of recent tasks owned by the persona (running=0.7,
///    failed=0.4, completed=0.15, idle=0.08), clamped to [0, 1];
///  • agent status = Working if any running task; Blocked on any incident;
///    Waiting on any approval; Done when last task completed; Idle otherwise.
///
/// Layout uses **policy-pinned sectors**: each <see cref="AgentType"/> owns
/// an angular wedge proportional to its member count, placed at a fixed
/// angle per type so operators build spatial memory (Code always NE,
/// Research NW, Data SW, Ops SE, Write N, Design S). Agents within a wedge
/// lay out on a fixed radius, sorted alphabetically by id so new members
/// slot in deterministically.
/// </summary>
public sealed class TopologyProjectionService : BackgroundService
{
    private readonly IExpertRegistry _experts;
    private readonly OverseerRegistry _overseers;
    private readonly IFleetTaskSource _tasks;
    private readonly TopologyState _topology;
    private readonly DataflowTracker _dataflow;
    private readonly ILogger<TopologyProjectionService> _logger;

    private readonly SemaphoreSlim _trigger = new(0, int.MaxValue);
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    public TopologyProjectionService(
        IExpertRegistry experts,
        OverseerRegistry overseers,
        IFleetTaskSource tasks,
        TopologyState topology,
        DataflowTracker dataflow,
        ILogger<TopologyProjectionService> logger)
    {
        _experts = experts;
        _overseers = overseers;
        _tasks = tasks;
        _topology = topology;
        _dataflow = dataflow;
        _logger = logger;

        _experts.OnChanged  += Schedule;
        _overseers.OnChanged += Schedule;
        _tasks.OnChanged     += Schedule;
        _dataflow.OnChanged  += Schedule;
    }

    private void Schedule()
    {
        // Release triggers the debounce loop; a burst of changes releases the
        // semaphore many times but WaitAsync + 300ms delay coalesces them.
        try { _trigger.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One projection on startup so the graph isn't empty before the first event.
        try { Project(); }
        catch (Exception ex) { _logger.LogWarning(ex, "initial topology projection failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _trigger.WaitAsync(stoppingToken);
                await Task.Delay(DebounceWindow, stoppingToken);
                // drain — anything that came in while we slept is already counted.
                while (_trigger.CurrentCount > 0)
                {
                    try { await _trigger.WaitAsync(stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
                Project();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "topology projection tick failed"); }
        }
    }

    internal void Project()
    {
        var experts = _experts.All;
        var overseers = _overseers.Snapshot();
        var tasks = _tasks.Snapshot();

        // ---- aggregate task signal per owner-persona ----
        var taskByOwner = tasks
            .GroupBy(t => t.OwnerPersona ?? "")
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var nodes = new List<TopologyNode>();
        var edges = new List<TopologyEdge>();

        // ---- helm (center) ----
        var totalWorkload = tasks.Count == 0 ? 0.0 :
            Math.Clamp(tasks.Count(t => t.Bucket is "Running" or "Approval" or "Incident") / 10.0, 0.1, 1.0);
        nodes.Add(new TopologyNode(
            Id: "helm", Label: "Lord Helm", Kind: TopologyKind.Helm,
            X: 50, Y: 50,
            Load: totalWorkload));

        // ---- synthesiser shadow-node (cross-cutting, helm-adjacent) ----
        var synth = experts.FirstOrDefault(e => e.Id.Equals("synthesiser", StringComparison.OrdinalIgnoreCase));
        if (synth is not null)
        {
            nodes.Add(new TopologyNode(
                Id: synth.Id, Label: synth.DisplayName,
                Kind: TopologyKind.Agent,
                X: 63, Y: 50,   // just east of helm
                AgentType: synth.Persona.AgentType,
                Status: StatusFor(taskByOwner, synth.Id),
                Load: LoadFor(taskByOwner, synth.Id)));
            edges.Add(new TopologyEdge("helm", synth.Id));
        }

        // ---- pod nodes + agent nodes per AgentType sector ----
        var bySector = experts
            .Where(e => !e.Id.Equals("synthesiser", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Persona.AgentType)
            .OrderBy(g => g.Key.ToString())
            .ToList();

        foreach (var group in bySector)
        {
            var (podX, podY, podAngleCenter) = SectorAnchor(group.Key);
            var podId = $"orc-{group.Key.ToString().ToLowerInvariant()}";
            var podLoad = Math.Clamp(group.Sum(e => LoadFor(taskByOwner, e.Id)) / Math.Max(1, group.Count() * 0.8), 0, 1);
            nodes.Add(new TopologyNode(
                Id: podId, Label: group.Key + " Pod",
                Kind: TopologyKind.Orc,
                X: podX, Y: podY,
                Load: podLoad));
            edges.Add(new TopologyEdge("helm", podId));

            // Lay out members on a short arc around the pod's center angle.
            var members = group.OrderBy(e => e.Id).ToList();
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                var (ax, ay) = MemberPosition(podAngleCenter, i, members.Count);
                nodes.Add(new TopologyNode(
                    Id: m.Id, Label: m.DisplayName,
                    Kind: TopologyKind.Agent,
                    X: ax, Y: ay,
                    AgentType: m.Persona.AgentType,
                    Status: StatusFor(taskByOwner, m.Id),
                    Load: LoadFor(taskByOwner, m.Id),
                    Task: taskByOwner.TryGetValue(m.Id, out var mt) && mt.Count > 0
                        ? mt.OrderByDescending(t => t.UpdatedAt).First().Label
                        : null));
                edges.Add(new TopologyEdge(podId, m.Id));
            }
        }

        // ---- overseers pod (distinct from persona pods — they're tick-based) ----
        if (overseers.Count > 0)
        {
            const string overPodId = "orc-overseers";
            nodes.Add(new TopologyNode(
                Id: overPodId, Label: "Overseers",
                Kind: TopologyKind.Orc,
                X: 50, Y: 85,
                Load: Math.Clamp(overseers.Count(s => s.LastStatus == OverseerStatus.Working) / Math.Max(1.0, overseers.Count), 0, 1)));
            edges.Add(new TopologyEdge("helm", overPodId));

            for (int i = 0; i < overseers.Count; i++)
            {
                var o = overseers[i];
                var angle = Math.PI + (i + 1) * Math.PI / (overseers.Count + 1); // lower half arc
                var x = 50 + 30 * Math.Cos(angle);
                var y = 50 + 30 * Math.Sin(angle);
                var status = o.LastStatus switch
                {
                    OverseerStatus.Working            => AgentRunState.Working,
                    OverseerStatus.WaitingForAttention => AgentRunState.Waiting,
                    OverseerStatus.Error              => AgentRunState.Blocked,
                    OverseerStatus.DoneForNow         => AgentRunState.Done,
                    _ => AgentRunState.Idle,
                };
                nodes.Add(new TopologyNode(
                    Id: o.AgentId, Label: o.AgentId,
                    Kind: TopologyKind.Agent,
                    X: x, Y: y,
                    AgentType: AgentType.Ops,
                    Status: status,
                    Load: Math.Clamp(o.TickCount / 50.0, 0.05, 1.0),
                    Task: o.LastMessage));
                edges.Add(new TopologyEdge(overPodId, o.AgentId));
            }
        }

        // ---- dataflow overlay (kind=Dataflow, observed-at stamps) ----
        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var obs in _dataflow.Recent())
        {
            var from = ResolveNodeId(obs.From, nodeIds);
            var to   = ResolveNodeId(obs.To,   nodeIds);
            if (from is null || to is null || from == to) continue;
            edges.Add(new TopologyEdge(from, to, EdgeKind.Dataflow, obs.ObservedAt));
        }

        _topology.ReplaceAll(nodes, edges);
        _logger.LogDebug("topology projected: {Nodes} nodes, {Edges} edges", nodes.Count, edges.Count);
    }

    // Accepts a raw persona id OR an engram-namespace form (expert_foo_bar) and
    // resolves to whatever the topology uses for that node. Returns null when
    // the endpoint isn't in the current projection.
    private static string? ResolveNodeId(string raw, HashSet<string> nodeIds)
    {
        if (nodeIds.Contains(raw)) return raw;
        if (raw.StartsWith("expert_", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = raw["expert_".Length..].Replace('_', '-');
            if (nodeIds.Contains(candidate)) return candidate;
        }
        return null;
    }

    // ---- positioning ------------------------------------------------------------

    // Each AgentType gets a stable angle around helm so operators build
    // muscle memory: Write=N, Code=NE, Research=NW, Data=SW, Ops=SE, Design=S.
    private static (double x, double y, double angleDeg) SectorAnchor(AgentType t) => t switch
    {
        AgentType.Write    => (50, 18, 270),  // N
        AgentType.Code     => (78, 28,   0),  // NE (east)
        AgentType.Research => (22, 28, 180),  // NW (west)
        AgentType.Ops      => (78, 72, 315),  // SE
        AgentType.Data     => (22, 72, 225),  // SW
        AgentType.Design   => (50, 82,  90),  // S
        _                  => (50, 50,   0),
    };

    // Lay members on a 10-wide arc around the pod, centered on its sector angle.
    private static (double x, double y) MemberPosition(double podAngleDeg, int index, int count)
    {
        // Arc span grows with count but never past ±45° so sectors don't collide.
        var spanDeg = Math.Min(90, Math.Max(10, count * 12));
        var startDeg = podAngleDeg - spanDeg / 2;
        var stepDeg  = count <= 1 ? 0 : spanDeg / (count - 1);
        var angleRad = (startDeg + index * stepDeg) * Math.PI / 180;
        const double radius = 40;
        return (50 + radius * Math.Cos(angleRad), 50 + radius * Math.Sin(angleRad));
    }

    // ---- task signal rollup ------------------------------------------------------

    private static double LoadFor(
        Dictionary<string, List<FleetTaskSnapshot>> taskByOwner, string personaId)
    {
        if (!taskByOwner.TryGetValue(personaId, out var list) || list.Count == 0) return 0.08;
        double load = 0;
        foreach (var t in list)
        {
            load += t.Bucket switch
            {
                "Running"   => 0.7,
                "Incident"  => 0.5,
                "Approval"  => 0.4,
                "Failed"    => 0.3,
                "Completed" => 0.15,
                _ => 0.08,
            };
        }
        return Math.Clamp(load / list.Count, 0.05, 1.0);
    }

    private static AgentRunState StatusFor(
        Dictionary<string, List<FleetTaskSnapshot>> taskByOwner, string personaId)
    {
        if (!taskByOwner.TryGetValue(personaId, out var list) || list.Count == 0)
            return AgentRunState.Idle;
        if (list.Any(t => t.Bucket == "Incident"))  return AgentRunState.Blocked;
        if (list.Any(t => t.Bucket == "Approval"))  return AgentRunState.Waiting;
        if (list.Any(t => t.Bucket == "Running"))   return AgentRunState.Working;
        if (list.Any(t => t.Bucket == "Failed"))    return AgentRunState.Blocked;
        if (list.Any(t => t.Bucket == "Completed")) return AgentRunState.Done;
        return AgentRunState.Idle;
    }

    public override void Dispose()
    {
        _experts.OnChanged  -= Schedule;
        _overseers.OnChanged -= Schedule;
        _tasks.OnChanged     -= Schedule;
        _dataflow.OnChanged  -= Schedule;
        _trigger.Dispose();
        base.Dispose();
    }
}
