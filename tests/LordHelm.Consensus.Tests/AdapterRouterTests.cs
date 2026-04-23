using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class AdapterRouterTests
{
    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public string? Next { get; set; } = "ok";
        public Exception? Throw { get; set; }
        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temperature, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(Next);
        }
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, AdapterCapabilities caps, IAgentOutcomeModelClient cli)
            : base(vendor, "m1", caps, cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    private static StubAdapter MakeAdapter(string vendor, ResourceMode mode, int ctx, bool tools, params string[] tasks) =>
        new(vendor,
            new AdapterCapabilities(
                tasks.Length == 0 ? new[] { "code" } : tasks,
                ctx, tools, true, true, mode,
                new CostProfile(1m, 2m, 0m)),
            new FakeCli());

    [Fact]
    public void Rank_Sorts_By_Total_Score()
    {
        var claude = MakeAdapter("claude", ResourceMode.Interactive, 1_000_000, true, "code", "reasoning");
        var gemini = MakeAdapter("gemini", ResourceMode.Batch, 1_000_000, true, "research");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { claude, gemini });
        var router = new AdapterRouter(registry);

        var ranked = router.Rank(new RoutingRequest(
            TaskKind: "code",
            EstimatedContextTokens: 10_000,
            NeedsToolCalls: true,
            PreferredMode: ResourceMode.Interactive));

        Assert.Equal("claude", ranked[0].VendorId);
        Assert.Equal("gemini", ranked[1].VendorId);
        Assert.True(ranked[0].Total > ranked[1].Total);
    }

    [Fact]
    public void Pin_Forces_Named_Vendor_To_Top()
    {
        var claude = MakeAdapter("claude", ResourceMode.Interactive, 1_000_000, true, "code");
        var codex  = MakeAdapter("codex",  ResourceMode.Builder,     200_000,   true, "code");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { claude, codex });
        var router = new AdapterRouter(registry);

        var top = router.Pick(new RoutingRequest("code", 50_000, true, ResourceMode.Interactive), pinnedVendor: "codex");

        Assert.NotNull(top);
        Assert.Equal("codex", top!.VendorId);
    }

    [Fact]
    public void ContextFit_Downweights_Adapters_With_Insufficient_Context()
    {
        var small = MakeAdapter("small", ResourceMode.Interactive, 8_000, false, "code");
        var large = MakeAdapter("large", ResourceMode.Interactive, 1_000_000, false, "code");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { small, large });
        var router = new AdapterRouter(registry);

        var ranked = router.Rank(new RoutingRequest("code", 500_000, false, ResourceMode.Interactive));

        Assert.Equal("large", ranked[0].VendorId);
        Assert.True(ranked.First(r => r.VendorId == "small").ContextFit < 0.1);
    }

    [Fact]
    public void ToolFit_Penalises_Adapter_Without_Tool_Support_When_Needed()
    {
        var withTools    = MakeAdapter("with",    ResourceMode.Interactive, 100_000, true,  "code");
        var withoutTools = MakeAdapter("without", ResourceMode.Interactive, 100_000, false, "code");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { withTools, withoutTools });
        var router = new AdapterRouter(registry);

        var ranked = router.Rank(new RoutingRequest("code", 10_000, true, ResourceMode.Interactive));

        Assert.Equal("with", ranked[0].VendorId);
        var withoutScore = ranked.First(r => r.VendorId == "without");
        Assert.Equal(0.1, withoutScore.ToolFit, 3);
    }

    [Fact]
    public void Fallbacks_Excludes_Preferred_Vendor()
    {
        var a = MakeAdapter("a", ResourceMode.Interactive, 100_000, true, "code");
        var b = MakeAdapter("b", ResourceMode.Builder,     100_000, true, "code");
        var c = MakeAdapter("c", ResourceMode.Batch,       100_000, true, "code");
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { a, b, c });
        var router = new AdapterRouter(registry);

        var fallbacks = router.Fallbacks(
            new RoutingRequest("code", 10_000, true, ResourceMode.Interactive),
            preferredVendor: "a");

        Assert.Equal(2, fallbacks.Count);
        Assert.DoesNotContain(fallbacks, f => f.VendorId == "a");
    }
}
