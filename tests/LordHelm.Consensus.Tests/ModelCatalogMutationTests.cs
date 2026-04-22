using LordHelm.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ModelCatalogMutationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ModelCatalogMutationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helm-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "models.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Upsert_Adds_A_New_Model_And_Resolve_Picks_It()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        var entry = new ModelEntry("anthropic-beta", "claude-opus-4-8",
            ModelTier.Deep, "experimental opus", true, DateTimeOffset.UtcNow);
        cat.Upsert(entry);

        var resolved = cat.Resolve(ModelTier.Deep);
        Assert.NotNull(resolved);
        Assert.Equal("claude-opus-4-8", resolved!.ModelId);
    }

    [Fact]
    public void Remove_Drops_The_Entry()
    {
        var cat = new ModelCatalog();
        var initial = cat.GetModels();
        Assert.Contains(initial, m => m.ModelId == "claude-opus-4-7");

        Assert.True(cat.Remove("claude", "claude-opus-4-7"));
        Assert.DoesNotContain(cat.GetModels(), m => m.ModelId == "claude-opus-4-7");
        Assert.False(cat.Remove("claude", "claude-opus-4-7"));
    }

    [Fact]
    public void OnChanged_Fires_On_Upsert_And_Remove()
    {
        var cat = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        var hits = 0;
        cat.OnChanged += () => hits++;

        cat.Upsert(new ModelEntry("v", "m", ModelTier.Fast, "", true, DateTimeOffset.UtcNow));
        Assert.Equal(1, hits);

        cat.Remove("v", "m");
        Assert.Equal(2, hits);
    }

    [Fact]
    public void Vendors_Returns_Distinct_Vendor_Ids()
    {
        var cat = new ModelCatalog();
        var vendors = cat.Vendors();
        Assert.Contains("claude", vendors);
        Assert.Contains("gemini", vendors);
        Assert.Contains("codex", vendors);
        Assert.Equal(vendors.Count, vendors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task JsonFileStore_Roundtrips_Catalog_To_Disk()
    {
        var cat = new ModelCatalog(seed: new[]
        {
            new ModelEntry("v1", "m1", ModelTier.Deep, "first",  true,  DateTimeOffset.UtcNow),
            new ModelEntry("v1", "m2", ModelTier.Fast, "second", false, DateTimeOffset.UtcNow),
        });
        cat.RegisterMcpTool(new McpToolEntry("engram", "search", "lookup"));

        var store = new JsonFileModelCatalogStore(_path, NullLogger<JsonFileModelCatalogStore>.Instance);
        await store.SaveAsync(cat);
        Assert.True(File.Exists(_path));

        // Load into a fresh catalog — exercises ReplaceAll.
        var cat2 = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        await store.LoadAsync(cat2);
        Assert.Equal(2, cat2.GetModels().Count);
        Assert.Single(cat2.GetMcpTools());
        Assert.Equal("m1", cat2.Resolve(ModelTier.Deep, "v1")?.ModelId);
    }

    [Fact]
    public async Task JsonFileStore_Load_On_Missing_File_Is_A_Noop()
    {
        var cat = new ModelCatalog(); // default seed
        var preCount = cat.GetModels().Count;

        var store = new JsonFileModelCatalogStore(
            Path.Combine(_tempDir, "does-not-exist.json"),
            NullLogger<JsonFileModelCatalogStore>.Instance);
        await store.LoadAsync(cat);

        Assert.Equal(preCount, cat.GetModels().Count);
    }
}
