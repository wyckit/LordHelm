using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class ModelCapabilityOverridesTests
{
    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public Task<string?> GenerateAsync(string m, string p, int t, float temp, CancellationToken ct) => Task.FromResult<string?>("ok");
        public Task<bool> IsAvailableAsync(string m, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, string model, AdapterCapabilities baseline, IModelCapabilityProvider? catalog)
            : base(vendor, model, baseline, new FakeCli(), new RateLimitGovernor(10, TimeSpan.FromMinutes(1)), catalog) { }
    }

    private static AdapterCapabilities DefaultCaps() => new(
        new[] { "code" }, 200_000, true, true, true, ResourceMode.Interactive,
        new CostProfile(10m, 40m, 1m));

    [Fact]
    public void ModelCatalog_Implements_ModelCapabilityProvider()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        cat.Upsert(new ModelEntry(
            "claude", "claude-haiku-4-5", ModelTier.Fast, "fast", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 200_000,
            InputPerMTokens: 1m,
            OutputPerMTokens: 5m,
            SupportsToolCalls: false,
            Mode: ResourceMode.Interactive));

        var ov = cat.TryGet("claude", "claude-haiku-4-5");
        Assert.NotNull(ov);
        Assert.Equal(200_000, ov!.MaxContextTokens);
        Assert.Equal(1m, ov.InputPerMTokens);
        Assert.False(ov.SupportsToolCalls);
    }

    [Fact]
    public void Catalog_Entry_With_No_Overrides_Returns_Null()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        cat.Upsert(new ModelEntry("v", "m", ModelTier.Deep, "", true, DateTimeOffset.UtcNow));
        Assert.Null(cat.TryGet("v", "m"));
    }

    [Fact]
    public void Adapter_Capabilities_Overlay_From_Catalog()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        cat.Upsert(new ModelEntry("test", "m1", ModelTier.Deep, "d", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 4_000_000,
            InputPerMTokens: 99m));

        var adapter = new StubAdapter("test", "m1", DefaultCaps(), cat);

        // baseline context was 200k; catalog bumps to 4M
        Assert.Equal(4_000_000, adapter.Capabilities.MaxContextTokens);
        // input cost overridden, output cost falls back to baseline
        Assert.Equal(99m, adapter.Capabilities.Cost.InputPerMTokens);
        Assert.Equal(40m, adapter.Capabilities.Cost.OutputPerMTokens);
    }

    [Fact]
    public void Adapter_ResolveCapabilities_Varies_Per_Model_Id()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        cat.Upsert(new ModelEntry("test", "big",   ModelTier.Deep, "", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 2_000_000));
        cat.Upsert(new ModelEntry("test", "small", ModelTier.Fast, "", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 8_000));

        var adapter = new StubAdapter("test", "big", DefaultCaps(), cat);
        Assert.Equal(2_000_000, adapter.ResolveCapabilities("big").MaxContextTokens);
        Assert.Equal(8_000,     adapter.ResolveCapabilities("small").MaxContextTokens);
    }

    [Fact]
    public void No_Catalog_Falls_Back_To_Baseline()
    {
        var adapter = new StubAdapter("test", "m1", DefaultCaps(), catalog: null);
        Assert.Equal(200_000, adapter.Capabilities.MaxContextTokens);
        Assert.Equal(10m, adapter.Capabilities.Cost.InputPerMTokens);
    }

    [Fact]
    public void Router_Scores_Adapters_At_ModelHint_Not_DefaultModel()
    {
        // Same vendor, two catalogued models: "pro" huge context, "mini" small.
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        cat.Upsert(new ModelEntry("v", "pro",  ModelTier.Deep, "", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 1_000_000));
        cat.Upsert(new ModelEntry("v", "mini", ModelTier.Fast, "", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 8_000));

        var adapter = new StubAdapter("v", "pro", DefaultCaps(), cat);
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { adapter });
        var router = new AdapterRouter(registry);

        // At the pro model the adapter has context 1M → fit 1.0
        var proRank = router.Rank(new RoutingRequest("code", 500_000, false, ResourceMode.Interactive,
            ModelHint: "pro"));
        // At the mini model it only has 8k → fit 0.016
        var miniRank = router.Rank(new RoutingRequest("code", 500_000, false, ResourceMode.Interactive,
            ModelHint: "mini"));

        Assert.Equal(1.0, proRank[0].ContextFit, 3);
        Assert.True(miniRank[0].ContextFit < 0.1);
    }
}
