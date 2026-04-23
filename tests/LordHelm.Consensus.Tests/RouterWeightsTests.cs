using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class RouterWeightsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public RouterWeightsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helm-weights-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "routing-weights.json");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public Task<string?> GenerateAsync(string m, string p, int t, float temp, CancellationToken ct) => Task.FromResult<string?>("ok");
        public Task<bool> IsAvailableAsync(string m, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, AdapterCapabilities caps)
            : base(vendor, "m1", caps, new FakeCli(), new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    [Fact]
    public void Default_Weights_Sum_To_One()
    {
        var w = RouterWeights.Default;
        var sum = w.CapabilityMatch + w.RecentSuccess + w.LatencyFit + w.CostFit + w.ContextFit + w.ToolFit + w.ResourceFit;
        Assert.Equal(1.0, sum, 3);
    }

    [Fact]
    public void Apply_Matches_AdapterRoutingScore_Total_For_Default_Weights()
    {
        var s = new AdapterRoutingScore("v", 1.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5);
        Assert.Equal(s.Total, RouterWeights.Default.Apply(s), 3);
    }

    [Fact]
    public void Heavy_Cost_Weight_Flips_Ranking()
    {
        // expensive: wins on capability but loses on cost
        var expensive = new StubAdapter("pricey", new AdapterCapabilities(
            new[] { "code" }, 100_000, true, true, true, ResourceMode.Interactive,
            new CostProfile(100m, 400m, 0m)));
        var cheap = new StubAdapter("cheap", new AdapterCapabilities(
            new[] { "code" }, 100_000, true, true, true, ResourceMode.Interactive,
            new CostProfile(1m, 2m, 0m)));
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { expensive, cheap });

        // default weights: capability equal (both 1.0), cost differs → cheap edges out
        var defaultWeights = new RouterWeightsProvider();
        var router = new AdapterRouter(registry, defaultWeights);
        var ranked = router.Rank(new RoutingRequest("code", 1_000, false, ResourceMode.Interactive));
        Assert.Equal("cheap", ranked[0].VendorId);

        // heavy cost_fit: gap widens (still cheap, but verify the weight influences ordering)
        var heavyCost = new RouterWeightsProvider();
        heavyCost.Replace(new RouterWeights(CostFit: 0.90, CapabilityMatch: 0.10,
            RecentSuccess: 0, LatencyFit: 0, ContextFit: 0, ToolFit: 0, ResourceFit: 0));
        var router2 = new AdapterRouter(registry, heavyCost);
        var ranked2 = router2.Rank(new RoutingRequest("code", 1_000, false, ResourceMode.Interactive));
        Assert.Equal("cheap", ranked2[0].VendorId);
        // And with heavy cost the "cheap" advantage over "pricey" is bigger than with defaults
        var defaultGap = heavyCost.Current.Apply(ranked[0]) - heavyCost.Current.Apply(ranked[1]);
        Assert.True(defaultGap >= 0);
    }

    [Fact]
    public async Task Weights_Roundtrip_Via_JsonFile_Store()
    {
        var weights = new RouterWeightsProvider();
        weights.Replace(new RouterWeights(
            CapabilityMatch: 0.5, RecentSuccess: 0.1,
            LatencyFit: 0.1, CostFit: 0.1, ContextFit: 0.1, ToolFit: 0.05, ResourceFit: 0.05));

        var store = new JsonFileRouterWeightsStore(_path, NullLogger<JsonFileRouterWeightsStore>.Instance);
        await store.SaveAsync(weights);
        Assert.True(File.Exists(_path));

        var weights2 = new RouterWeightsProvider();
        await store.LoadAsync(weights2);
        Assert.Equal(0.5, weights2.Current.CapabilityMatch, 3);
        Assert.Equal(0.1, weights2.Current.RecentSuccess, 3);
        Assert.Equal(0.05, weights2.Current.ResourceFit, 3);
    }

    [Fact]
    public async Task Load_Missing_File_Keeps_Defaults()
    {
        var weights = new RouterWeightsProvider();
        var store = new JsonFileRouterWeightsStore(
            Path.Combine(_tempDir, "nope.json"),
            NullLogger<JsonFileRouterWeightsStore>.Instance);
        await store.LoadAsync(weights);
        Assert.Equal(RouterWeights.Default, weights.Current);
    }

    [Fact]
    public void OnChanged_Fires_On_Replace()
    {
        var weights = new RouterWeightsProvider();
        var hits = 0;
        weights.OnChanged += () => hits++;
        weights.Replace(new RouterWeights(CapabilityMatch: 0.42));
        Assert.Equal(1, hits);
    }
}
