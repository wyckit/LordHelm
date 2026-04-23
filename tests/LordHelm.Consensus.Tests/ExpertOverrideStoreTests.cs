using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ExpertOverrideStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ExpertOverrideStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helm-experts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "experts.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
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
            new AdapterCapabilities(new[] { "code" }, 100_000, true, true, true, ResourceMode.Interactive,
                new CostProfile(1m, 2m, 0m)),
            new FakeCli(), new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    private static ExpertRegistry BuildRegistry()
    {
        var router = new AdapterRouter(new AgentAdapterRegistry(new IAgentModelAdapter[] { new StubAdapter("claude") }));
        return new ExpertRegistry(ExpertDirectory.Default(), router);
    }

    [Fact]
    public async Task Save_Then_Load_Roundtrips_Overrides()
    {
        var reg = BuildRegistry();
        reg.Upsert("code-auditor",
            new ExpertPolicy(PreferredMode: ResourceMode.Batch, RequiresApproval: true, PinnedVendor: "gemini"),
            new ExpertBudget(MaxTokensPerCall: 512, MaxTokensPerGoal: 10_000, MaxUsdPerGoal: 3m));

        var store = new JsonFileExpertOverrideStore(_path, NullLogger<JsonFileExpertOverrideStore>.Instance);
        await store.SaveAsync(reg);
        Assert.True(File.Exists(_path));

        var reg2 = BuildRegistry();
        await store.LoadAsync(reg2);

        var e = reg2.Get("code-auditor")!;
        Assert.Equal(ResourceMode.Batch, e.Policy.PreferredMode);
        Assert.True(e.Policy.RequiresApproval);
        Assert.Equal("gemini", e.Policy.PinnedVendor);
        Assert.Equal(512, e.Budget.MaxTokensPerCall);
        Assert.Equal(10_000, e.Budget.MaxTokensPerGoal);
        Assert.Equal(3m, e.Budget.MaxUsdPerGoal);
    }

    [Fact]
    public async Task Load_On_Missing_File_Is_A_Noop()
    {
        var reg = BuildRegistry();
        var preCount = reg.GetOverrides().Count;

        var store = new JsonFileExpertOverrideStore(
            Path.Combine(_tempDir, "nope.json"),
            NullLogger<JsonFileExpertOverrideStore>.Instance);
        await store.LoadAsync(reg);

        Assert.Equal(preCount, reg.GetOverrides().Count);
    }

    [Fact]
    public async Task ReplaceAll_Clears_Then_Reseeds_On_Load()
    {
        var reg = BuildRegistry();
        reg.Upsert("code-auditor", new ExpertPolicy(PinnedVendor: "claude"), new ExpertBudget());
        var store = new JsonFileExpertOverrideStore(_path, NullLogger<JsonFileExpertOverrideStore>.Instance);
        await store.SaveAsync(reg);

        // Second registry with a DIFFERENT override — load must replace (not merge).
        var reg2 = BuildRegistry();
        reg2.Upsert("security-analyst", new ExpertPolicy(PinnedVendor: "gemini"), new ExpertBudget());
        await store.LoadAsync(reg2);

        Assert.Single(reg2.GetOverrides());
        Assert.True(reg2.GetOverrides().ContainsKey("code-auditor"));
        Assert.False(reg2.GetOverrides().ContainsKey("security-analyst"));
    }
}
