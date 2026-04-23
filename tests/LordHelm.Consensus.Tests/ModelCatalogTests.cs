using LordHelm.Orchestrator;

namespace LordHelm.Consensus.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void Default_Seed_Covers_All_Three_Vendors()
    {
        var cat = new ModelCatalog();
        var vendors = cat.GetModels().Select(m => m.VendorId).Distinct().ToHashSet();
        Assert.Contains("claude", vendors);
        Assert.Contains("gemini", vendors);
        Assert.Contains("codex", vendors);
    }

    [Fact]
    public void Resolve_Fast_Tier_Returns_A_Fast_Model()
    {
        var cat = new ModelCatalog();
        var m = cat.Resolve(ModelTier.Fast);
        Assert.NotNull(m);
        Assert.Equal(ModelTier.Fast, m!.Tier);
        Assert.True(m.IsAvailable);
    }

    [Fact]
    public void Resolve_Biases_By_Preferred_Vendor_When_Set()
    {
        var cat = new ModelCatalog();
        var claudeDeep = cat.Resolve(ModelTier.Deep, preferredVendor: "claude");
        var geminiDeep = cat.Resolve(ModelTier.Deep, preferredVendor: "gemini");
        Assert.NotNull(claudeDeep);
        Assert.NotNull(geminiDeep);
        Assert.Equal("claude", claudeDeep!.VendorId);
        Assert.Equal("gemini", geminiDeep!.VendorId);
    }

    [Fact]
    public void Resolve_Prefers_Most_Recently_Probed_When_No_Vendor_Hint()
    {
        // Auto-resolve used to alphabetise, so `claude-*` always beat `gpt-*`.
        // The new tie-break is LastProbed desc so a freshly-refreshed vendor
        // wins over a stale one regardless of ModelId ordering.
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var fresh = DateTimeOffset.UtcNow;
        cat.Upsert(new ModelEntry("claude", "claude-opus-4-7", ModelTier.Deep, "older", true, old));
        cat.Upsert(new ModelEntry("codex",  "gpt-5.4",         ModelTier.Deep, "fresh", true, fresh));

        var picked = cat.Resolve(ModelTier.Deep, preferredVendor: null);

        Assert.NotNull(picked);
        Assert.Equal("codex", picked!.VendorId);
        Assert.Equal("gpt-5.4", picked.ModelId);
    }

    [Fact]
    public void MarkAvailability_False_Drops_From_Resolve()
    {
        var cat = new ModelCatalog();
        var before = cat.Resolve(ModelTier.Fast, preferredVendor: "gemini");
        Assert.NotNull(before);
        cat.MarkAvailability("gemini", before!.ModelId, false);
        var after = cat.Resolve(ModelTier.Fast, preferredVendor: "gemini");
        Assert.NotEqual(before.ModelId, after?.ModelId);
    }

    [Fact]
    public void RegisterMcpTool_Surfaces_In_GetMcpTools()
    {
        var cat = new ModelCatalog();
        cat.RegisterMcpTool(new McpToolEntry("engram-memory", "store_memory", "write a node"));
        var tools = cat.GetMcpTools("engram-memory");
        Assert.Single(tools);
        Assert.Equal("store_memory", tools[0].ToolName);
    }
}
