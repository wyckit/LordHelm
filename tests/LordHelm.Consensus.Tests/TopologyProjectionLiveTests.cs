using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Overseers;
using LordHelm.Orchestrator.Topology;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

/// <summary>
/// Spot-checks that the TopologyProjectionService picks up live signals from
/// the real registries and actually mutates TopologyState in the shape the
/// Fleet Roster widget expects. Runs entirely in-memory — no Kestrel, no
/// engram MCP, no subprocess — so it belongs in the fast-tier spotcheck.
/// </summary>
public class TopologyProjectionLiveTests
{
    private sealed class StubTaskSource : IFleetTaskSource
    {
        private readonly List<FleetTaskSnapshot> _items = new();
        public event Action? OnChanged;
        public IReadOnlyList<FleetTaskSnapshot> Snapshot() => _items.ToList();
        public void Push(FleetTaskSnapshot s) { _items.Add(s); OnChanged?.Invoke(); }
        public void Clear() { _items.Clear(); OnChanged?.Invoke(); }
    }

    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public Task<string?> GenerateAsync(string m, string p, int t, float temp, CancellationToken ct) => Task.FromResult<string?>("ok");
        public Task<bool> IsAvailableAsync(string m, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor) : base(vendor, "m1",
            new AdapterCapabilities(new[] { "code" }, 100_000, true, true, true,
                ResourceMode.Interactive, new CostProfile(1m, 2m, 0m)),
            new FakeCli(), new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    private static (TopologyProjectionService svc, TopologyState topology, ExpertRegistry experts,
                    OverseerRegistry overseers, StubTaskSource tasks, DataflowTracker dataflow)
        BuildFixture()
    {
        var adapters = new IAgentModelAdapter[] { new StubAdapter("claude") };
        var router = new AdapterRouter(new AgentAdapterRegistry(adapters));
        var experts = new ExpertRegistry(ExpertDirectory.Default(), router);
        var overseers = new OverseerRegistry();
        var tasks = new StubTaskSource();
        var topology = new TopologyState();
        var dataflow = new DataflowTracker();
        var svc = new TopologyProjectionService(experts, overseers, tasks, topology, dataflow,
            NullLogger<TopologyProjectionService>.Instance);
        return (svc, topology, experts, overseers, tasks, dataflow);
    }

    [Fact]
    public void Project_Populates_Helm_Pods_And_Every_Default_Persona()
    {
        var (svc, topology, _, _, _, _) = BuildFixture();
        svc.Project();

        var nodes = topology.Nodes;
        Assert.Contains(nodes, n => n.Id == "helm" && n.Kind == TopologyKind.Helm);
        // Every default persona should surface as an Agent node.
        foreach (var expectedId in new[] { "code-auditor", "refactor-engineer", "security-analyst",
                                           "sandbox-runner", "tech-writer", "synthesiser" })
        {
            Assert.Contains(nodes, n => n.Id == expectedId && n.Kind == TopologyKind.Agent);
        }
        // One pod per AgentType that has ≥1 member.
        Assert.Contains(nodes, n => n.Id == "orc-code"     && n.Kind == TopologyKind.Orc);
        Assert.Contains(nodes, n => n.Id == "orc-research" && n.Kind == TopologyKind.Orc);
    }

    [Fact]
    public void Running_Task_For_Persona_Lights_Up_Its_Node_As_Working_With_Load()
    {
        var (svc, topology, _, _, tasks, _) = BuildFixture();

        tasks.Push(new FleetTaskSnapshot(
            WidgetId: "goal-abc::root",
            Label:   "audit Foo.cs [code-auditor via claude #m0]",
            Bucket:  "Running",
            Status:  "running",
            OwnerPersona: "code-auditor",
            UpdatedAt: DateTimeOffset.UtcNow));
        svc.Project();

        var node = topology.Nodes.First(n => n.Id == "code-auditor");
        Assert.Equal(AgentRunState.Working, node.Status);
        Assert.True(node.Load > 0.5, $"expected load > 0.5, got {node.Load}");
    }

    [Fact]
    public void Approval_For_Persona_Maps_To_Waiting_Status()
    {
        var (svc, topology, _, _, tasks, _) = BuildFixture();

        tasks.Push(new FleetTaskSnapshot(
            WidgetId: "approval-1",
            Label: "expert approval: security-analyst",
            Bucket: "Approval",
            Status: "pending-approval",
            OwnerPersona: "security-analyst",
            UpdatedAt: DateTimeOffset.UtcNow));
        svc.Project();

        var node = topology.Nodes.First(n => n.Id == "security-analyst");
        Assert.Equal(AgentRunState.Waiting, node.Status);
    }

    [Fact]
    public void Incident_For_Persona_Maps_To_Blocked_Status()
    {
        var (svc, topology, _, _, tasks, _) = BuildFixture();
        tasks.Push(new FleetTaskSnapshot(
            "incident-1", "ops crash [sandbox-runner via codex #m0]",
            "Incident", "incident", "sandbox-runner", DateTimeOffset.UtcNow));
        svc.Project();
        Assert.Equal(AgentRunState.Blocked, topology.Nodes.First(n => n.Id == "sandbox-runner").Status);
    }

    [Fact]
    public void Idle_Persona_Stays_Idle_With_Floor_Load()
    {
        var (svc, topology, _, _, _, _) = BuildFixture();
        svc.Project();
        var node = topology.Nodes.First(n => n.Id == "tech-writer");
        Assert.Equal(AgentRunState.Idle, node.Status);
        Assert.True(node.Load <= 0.1, $"idle load expected tiny, got {node.Load}");
    }

    [Fact]
    public void Dataflow_Observation_Produces_Dataflow_Edge_With_ObservedAt()
    {
        var (svc, topology, _, _, _, dataflow) = BuildFixture();
        dataflow.Observe("code-auditor", "synthesiser", reason: "test");
        svc.Project();

        var edge = topology.Edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Dataflow &&
            e.From == "code-auditor" &&
            e.To   == "synthesiser");
        Assert.NotNull(edge);
        Assert.NotNull(edge!.ObservedAt);
        Assert.True((DateTimeOffset.UtcNow - edge.ObservedAt!.Value).TotalSeconds < 5);
    }

    [Fact]
    public void Dataflow_With_Engram_Namespace_Form_Resolves_To_Persona_Id()
    {
        // GoalRunner emits observations using expert_{ns} form in some paths;
        // the projection's ResolveNodeId should normalise back to persona id.
        var (svc, topology, _, _, _, dataflow) = BuildFixture();
        dataflow.Observe("expert_code_auditor", "expert_synthesiser", reason: "test");
        svc.Project();

        Assert.Contains(topology.Edges, e =>
            e.Kind == EdgeKind.Dataflow && e.From == "code-auditor" && e.To == "synthesiser");
    }

    [Fact]
    public void Overseer_Shows_Under_Dedicated_Pod_With_Status_Mapped()
    {
        var (svc, topology, _, overseers, _, _) = BuildFixture();
        overseers.Register(new FakeOverseer("doc-curator"), enabledByDefault: true);
        overseers.RecordTick("doc-curator",
            new OverseerResult(OverseerStatus.Working, "scanned 3 files"),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        svc.Project();

        Assert.Contains(topology.Nodes, n => n.Id == "orc-overseers" && n.Kind == TopologyKind.Orc);
        var node = topology.Nodes.First(n => n.Id == "doc-curator");
        Assert.Equal(AgentRunState.Working, node.Status);
    }

    private sealed class FakeOverseer : IOverseerAgent
    {
        public FakeOverseer(string id) { Id = id; }
        public string Id { get; }
        public string Name => Id;
        public string Description => "test overseer";
        public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
        public Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct) =>
            Task.FromResult(new OverseerResult(OverseerStatus.WaitingForAttention, "idle"));
    }
}
