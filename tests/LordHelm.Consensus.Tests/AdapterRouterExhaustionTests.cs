using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

/// <summary>
/// Panel-endorsed behaviour: when a vendor's subscription is exhausted,
/// the router HARD-EXCLUDES it from the scoring pool until the exhaustion
/// monitor confirms recovery — not just deprioritises via score weight.
/// </summary>
public class AdapterRouterExhaustionTests
{
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

    [Fact]
    public void Rank_Excludes_Exhausted_Vendor_From_Pool()
    {
        var claude = new StubAdapter("claude");
        var gemini = new StubAdapter("gemini");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { claude, gemini });
        var usage = new UsageState();
        usage.Update(new UsageSnapshot("claude", null, null, null, null, null, null,
            AuthOk: false, Exhausted: true, ResolvedModel: null,
            RawOutput: null, Error: "quota", ProbedAt: DateTimeOffset.UtcNow));
        var router = new AdapterRouter(registry, usage: usage);

        var ranked = router.Rank(new RoutingRequest(
            TaskKind: "code", EstimatedContextTokens: 1000,
            NeedsToolCalls: false, PreferredMode: ResourceMode.Interactive));

        Assert.All(ranked, s => Assert.NotEqual("claude", s.VendorId));
        Assert.Contains(ranked, s => s.VendorId == "gemini");
    }

    [Fact]
    public void Rank_Falls_Back_To_All_When_Every_Vendor_Exhausted()
    {
        // Better to fail a call than route nowhere; the pool cannot be empty.
        var claude = new StubAdapter("claude");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { claude });
        var usage = new UsageState();
        usage.Update(new UsageSnapshot("claude", null, null, null, null, null, null,
            AuthOk: false, Exhausted: true, ResolvedModel: null,
            RawOutput: null, Error: "quota", ProbedAt: DateTimeOffset.UtcNow));
        var router = new AdapterRouter(registry, usage: usage);

        var ranked = router.Rank(new RoutingRequest(
            TaskKind: "code", EstimatedContextTokens: 1000,
            NeedsToolCalls: false, PreferredMode: ResourceMode.Interactive));

        Assert.Single(ranked);
        Assert.Equal("claude", ranked[0].VendorId);
    }
}
