using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

/// <summary>
/// ChatDispatchRequest.Thinking must flow through to GoalRunRequest.Thinking
/// so GoalRunner can apply the reasoning preamble to downstream LLM calls.
/// </summary>
public class ChatDispatcherThinkingTests
{
    private sealed class CapturingGoalRunner : IGoalRunner
    {
        public GoalRunRequest? LastRequest { get; private set; }
        public Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default)
        {
            LastRequest = req;
            return Task.FromResult(new GoalRunResult(
                GoalId: "g1", Succeeded: true, DagNodeCount: 1,
                NodeOutputs: new Dictionary<string, string> { ["n1"] = "ok" },
                Synthesis: "done",
                ErrorDetail: null));
        }
    }

    private sealed class StaticRouter : IChatRouter
    {
        public Task<ChatRoutingPlan> RouteAsync(string text, IReadOnlyList<string> recent, CancellationToken ct = default)
            => Task.FromResult(new ChatRoutingPlan(
                Kind: ChatRouteKind.DecomposeAndDispatch,
                PersonaHints: Array.Empty<string>(),
                Tier: null, ModelHint: null,
                NeedsPanel: false, PanelSize: 0,
                SkillHints: Array.Empty<string>(),
                RiskTier: null, Rationale: "static"));
    }

    private sealed class EmptyExpertRegistry : IExpertRegistry
    {
        public IReadOnlyList<IExpert> All => Array.Empty<IExpert>();
        public IExpert? Get(string id) => null;
        public void Upsert(string id, ExpertPolicy policy, ExpertBudget budget) { }
        public IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> GetOverrides() =>
            new Dictionary<string, (ExpertPolicy, ExpertBudget)>();
        public void ReplaceAll(IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> overrides) { }
        public event Action? OnChanged { add { } remove { } }
    }

    private static ChatDispatcher NewDispatcher(CapturingGoalRunner runner)
    {
        var registry = new EmptyExpertRegistry();
        return new ChatDispatcher(
            new StaticRouter(),
            new SafetyFloor(registry, NullLogger<SafetyFloor>.Instance),
            runner,
            registry,
            NullLogger<ChatDispatcher>.Instance);
    }

    [Fact]
    public async Task Dispatcher_Threads_Thinking_True_Into_GoalRunRequest()
    {
        var runner = new CapturingGoalRunner();
        var disp = NewDispatcher(runner);

        await disp.DispatchAsync(new ChatDispatchRequest(
            Text: "summarise the fleet", SkipRouter: true,
            ExplicitVendor: "claude", ExplicitModel: "claude-opus-4-7",
            Thinking: true));

        Assert.NotNull(runner.LastRequest);
        Assert.True(runner.LastRequest!.Thinking);
        Assert.Equal("claude", runner.LastRequest.PreferredVendor);
    }

    [Fact]
    public async Task Dispatcher_Defaults_Thinking_False()
    {
        var runner = new CapturingGoalRunner();
        var disp = NewDispatcher(runner);

        await disp.DispatchAsync(new ChatDispatchRequest(
            Text: "quick status", SkipRouter: true,
            ExplicitVendor: "claude", ExplicitModel: "claude-haiku-4-5"));

        Assert.NotNull(runner.LastRequest);
        Assert.False(runner.LastRequest!.Thinking);
    }
}
