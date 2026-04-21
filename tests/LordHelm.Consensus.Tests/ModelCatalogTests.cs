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
