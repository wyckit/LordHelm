using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class ExpertTests
{
    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public string? Next { get; set; } = "hi";
        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temperature, CancellationToken ct)
            => Task.FromResult(Next);
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, IAgentOutcomeModelClient cli)
            : base(vendor, "m1",
                new AdapterCapabilities(
                    new[] { "code", "reasoning" }, 100_000, true, true, true, ResourceMode.Interactive,
                    new CostProfile(1m, 2m, 0m)),
                cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    private static IAdapterRouter BuildRouter(params string[] vendors)
    {
        var adapters = vendors.Select(v => (IAgentModelAdapter)new StubAdapter(v, new FakeCli { Next = $"out-{v}" })).ToArray();
        return new AdapterRouter(new AgentAdapterRegistry(adapters));
    }

    private static ExpertPersona Persona(string id = "tester") =>
        new(Id: id, Name: "Test " + id, PreferredVendor: "claude", Model: "m1",
            SystemHint: "be brief", PreferredSkills: new[] { "reasoning" });

    [Fact]
    public async Task ActAsync_Picks_Adapter_And_Returns_Output()
    {
        var router = BuildRouter("claude", "gemini", "codex");
        var expert = new Expert(Persona(), new ExpertPolicy(), new ExpertBudget(), router);

        var r = await expert.ActAsync(new ExpertActRequest("design a schema"));

        Assert.True(r.Succeeded);
        Assert.Equal("out-claude", r.Output);
        Assert.Equal("claude", r.VendorUsed);
        Assert.False(r.BudgetExceeded);
    }

    [Fact]
    public async Task ActAsync_Honours_PinnedVendor()
    {
        var router = BuildRouter("claude", "gemini", "codex");
        var expert = new Expert(
            Persona(),
            new ExpertPolicy(PinnedVendor: "gemini"),
            new ExpertBudget(),
            router);

        var r = await expert.ActAsync(new ExpertActRequest("review"));
        Assert.Equal("gemini", r.VendorUsed);
    }

    [Fact]
    public async Task ActAsync_Blocks_When_Budget_Exhausted()
    {
        var router = BuildRouter("claude");
        var expert = new Expert(
            Persona(),
            new ExpertPolicy(),
            new ExpertBudget(MaxTokensPerCall: 32, MaxTokensPerGoal: 4),
            router);

        var first  = await expert.ActAsync(new ExpertActRequest("t1"));
        var second = await expert.ActAsync(new ExpertActRequest("t2"));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.True(second.BudgetExceeded);
        Assert.Equal("budget_exceeded_per_goal", second.Error);
    }

    [Fact]
    public void Expert_Namespace_Follows_Convention()
    {
        var router = BuildRouter("claude");
        var expert = new Expert(Persona("code-auditor"), new ExpertPolicy(), new ExpertBudget(), router);

        Assert.Equal("expert_code_auditor", expert.EngramNamespace);
    }

    [Fact]
    public void Registry_Enumerates_Personas_As_Experts_With_Defaults()
    {
        var router = BuildRouter("claude");
        var directory = ExpertDirectory.Default();
        var registry = new ExpertRegistry(directory, router);

        var all = registry.All;
        Assert.NotEmpty(all);
        Assert.Contains(all, e => e.Id == "code-auditor");
        Assert.Contains(all, e => e.Id == "synthesiser");
        Assert.Equal(ResourceMode.Interactive, registry.Get("code-auditor")!.Policy.PreferredMode);
    }

    [Fact]
    public void Registry_Upsert_Overrides_Policy_And_Budget()
    {
        var router = BuildRouter("claude");
        var directory = ExpertDirectory.Default();
        var registry = new ExpertRegistry(directory, router);

        registry.Upsert("security-analyst",
            new ExpertPolicy(PreferredMode: ResourceMode.Batch, RequiresApproval: true),
            new ExpertBudget(MaxUsdPerGoal: 20m));

        var e = registry.Get("security-analyst")!;
        Assert.Equal(ResourceMode.Batch, e.Policy.PreferredMode);
        Assert.True(e.Policy.RequiresApproval);
        Assert.Equal(20m, e.Budget.MaxUsdPerGoal);
    }

    private sealed class FakeApprovalGate : IApprovalGate
    {
        public bool Approve { get; set; } = true;
        public string Reason { get; set; } = "ok";
        public int Calls { get; private set; }
        public HostActionRequest? LastRequest { get; private set; }

        public Task<ApprovalDecision> RequestAsync(HostActionRequest req, CancellationToken ct = default)
        {
            Calls++;
            LastRequest = req;
            return Task.FromResult(new ApprovalDecision(Approve, Reason, DateTimeOffset.UtcNow, false));
        }
        public void GrantBatchToken(string sessionId, RiskTier maxTier, TimeSpan window) { }
    }

    [Fact]
    public async Task ActAsync_RequiresApproval_Succeeds_When_Gate_Approves()
    {
        var router = BuildRouter("claude");
        var gate = new FakeApprovalGate { Approve = true, Reason = "operator ok" };
        var expert = new Expert(Persona(), new ExpertPolicy(RequiresApproval: true), new ExpertBudget(), router,
            engram: null, approvalGate: gate);

        var r = await expert.ActAsync(new ExpertActRequest("ship it"));

        Assert.True(r.Succeeded);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(RiskTier.Exec, gate.LastRequest!.RiskTier);
        Assert.Equal("expert:tester", gate.LastRequest.SkillId);
    }

    [Fact]
    public async Task ActAsync_RequiresApproval_Fails_When_Gate_Denies()
    {
        var router = BuildRouter("claude");
        var gate = new FakeApprovalGate { Approve = false, Reason = "timed out" };
        var expert = new Expert(Persona(), new ExpertPolicy(RequiresApproval: true), new ExpertBudget(), router,
            engram: null, approvalGate: gate);

        var r = await expert.ActAsync(new ExpertActRequest("ship it"));

        Assert.False(r.Succeeded);
        Assert.Contains("approval_denied", r.Error);
        Assert.Contains("timed out", r.Error);
    }

    [Fact]
    public async Task ActAsync_RequiresApproval_Fails_When_Gate_Is_Null()
    {
        var router = BuildRouter("claude");
        var expert = new Expert(Persona(), new ExpertPolicy(RequiresApproval: true), new ExpertBudget(), router,
            engram: null, approvalGate: null);

        var r = await expert.ActAsync(new ExpertActRequest("ship it"));

        Assert.False(r.Succeeded);
        Assert.Equal("approval_gate_unavailable", r.Error);
    }

    [Fact]
    public async Task ActAsync_No_Approval_Required_Skips_Gate()
    {
        var router = BuildRouter("claude");
        var gate = new FakeApprovalGate();
        var expert = new Expert(Persona(), new ExpertPolicy(RequiresApproval: false), new ExpertBudget(), router,
            engram: null, approvalGate: gate);

        await expert.ActAsync(new ExpertActRequest("hi"));
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public void Registry_OnChanged_Fires_On_Upsert_And_ReplaceAll()
    {
        var router = BuildRouter("claude");
        var registry = new ExpertRegistry(ExpertDirectory.Default(), router);
        var hits = 0;
        registry.OnChanged += () => hits++;

        registry.Upsert("code-auditor", new ExpertPolicy(RequiresApproval: true), new ExpertBudget());
        Assert.Equal(1, hits);

        registry.ReplaceAll(new Dictionary<string, (ExpertPolicy, ExpertBudget)>());
        Assert.Equal(2, hits);
    }

    [Fact]
    public void ResetBudgetWindow_Zeroes_Token_Counter()
    {
        var router = BuildRouter("claude");
        var expert = new Expert(Persona(), new ExpertPolicy(), new ExpertBudget(), router);
        Assert.Equal(0, expert.TokensUsedThisGoal);
        expert.ResetBudgetWindow();
        Assert.Equal(0, expert.TokensUsedThisGoal);
    }
}
