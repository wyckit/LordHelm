using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.ModelDiscovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ModelProbeTests
{
    private sealed class StaticProber : IModelProber
    {
        public string VendorId { get; }
        private readonly ProbeResult _result;
        public StaticProber(string vendor, ProbeResult result) { VendorId = vendor; _result = result; }
        public Task<ProbeResult> ProbeAsync(CancellationToken ct = default) => Task.FromResult(_result);
    }

    [Fact]
    public void NumberedList_Parser_Handles_Codex_Sample()
    {
        var raw = @"
› 1. gpt-5.4 (current)   Latest frontier agentic coding model.
  2. gpt-5.2-codex       Frontier agentic coding model.
  3. gpt-5.1-codex-max   Codex-optimized flagship for deep and fast reasoning.
  4. gpt-5.4-mini        Smaller frontier agentic coding model.
  5. gpt-5.3-codex       Frontier Codex-optimized agentic coding model.
  6. gpt-5.2             Optimized for professional work and long-running agents
  7. gpt-5.1-codex-mini  Optimized for codex. Cheaper, faster, but less capable.
";
        var parser = new NumberedListModelParser();
        var models = parser.Parse(raw);

        Assert.Equal(7, models.Count);
        Assert.Equal("gpt-5.4",           models[0].ModelId);
        Assert.Equal("gpt-5.2-codex",     models[1].ModelId);
        Assert.Equal("gpt-5.1-codex-max", models[2].ModelId);
        Assert.Contains("frontier", models[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Codex flagship — no codex/mini in id → Deep even if desc says "agentic coding"
    [InlineData("gpt-5.4",            "Latest frontier agentic coding model.",              ModelTier.Deep)]
    [InlineData("gpt-5.2",            "Optimized for professional work.",                   ModelTier.Deep)]
    // `-max` overrides `-codex` → Deep (the `-codex-max` flagship case)
    [InlineData("gpt-5.1-codex-max",  "Codex-optimized flagship for deep and fast reasoning", ModelTier.Deep)]
    // Explicit `-codex` suffix → Code
    [InlineData("gpt-5.2-codex",      "Frontier agentic coding model.",                     ModelTier.Code)]
    [InlineData("gpt-5.3-codex",      "Frontier Codex-optimized agentic coding model.",     ModelTier.Code)]
    // `mini` → Fast regardless of codex suffix
    [InlineData("gpt-5.4-mini",       "Smaller frontier agentic coding model.",             ModelTier.Fast)]
    [InlineData("gpt-5.1-codex-mini", "Optimized for codex. Cheaper, faster, less capable", ModelTier.Fast)]
    [InlineData("claude-haiku-4-5",   "Haiku — fast + cheap.",                              ModelTier.Fast)]
    [InlineData("gemini-2.5-flash",   "fast tier",                                          ModelTier.Fast)]
    [InlineData("claude-opus-4-7",    "deepest reasoning",                                  ModelTier.Deep)]
    public void InferTier_Maps_Known_Patterns(string id, string desc, ModelTier expected)
    {
        Assert.Equal(expected, NumberedListModelParser.InferTier(id, desc));
    }

    [Fact]
    public void Parser_Ignores_Duplicates_And_Blank_Input()
    {
        var parser = new NumberedListModelParser();
        Assert.Empty(parser.Parse(""));
        Assert.Empty(parser.Parse("just some prose no numbered list"));

        var raw = "1. gpt-5.4 desc\n2. gpt-5.4 desc again"; // dup id
        var models = parser.Parse(raw);
        Assert.Single(models);
    }

    [Fact]
    public void Parser_Strips_Backticks_And_Quotes_From_Ids()
    {
        var parser = new NumberedListModelParser();
        var raw = "1. `gpt-5.4` flagship\n2. \"claude-opus-4-7\" deep\n3. *gemini-2.5-pro* google";
        var models = parser.Parse(raw);
        Assert.Equal(3, models.Count);
        Assert.Equal("gpt-5.4",          models[0].ModelId);
        Assert.Equal("claude-opus-4-7",  models[1].ModelId);
        Assert.Equal("gemini-2.5-pro",   models[2].ModelId);
    }

    [Theory]
    [InlineData("1. none")]
    [InlineData("1. unknown")]
    [InlineData("1. Output  prose that the LLM emitted")]
    public void Parser_Rejects_Obviously_Wrong_Captures(string raw)
    {
        var parser = new NumberedListModelParser();
        Assert.Empty(parser.Parse(raw));
    }

    [Fact]
    public async Task Refresher_Merges_Probed_Models_Into_Catalog()
    {
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        var prober = new StaticProber("codex", new ProbeResult("codex", true, new[]
        {
            new ProbedModel("gpt-5.4",       "Latest frontier",    ModelTier.Code),
            new ProbedModel("gpt-5.4-mini",  "Smaller frontier",   ModelTier.Fast),
            new ProbedModel("gpt-5.2",       "Prof work",          ModelTier.Deep),
        }, null));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, catalog,
            new ModelCatalogRefresherOptions(),
            NullLogger<ModelCatalogRefresher>.Instance);

        var results = await refresher.RefreshAllAsync();

        Assert.Single(results);
        Assert.True(results[0].Succeeded);
        var models = catalog.GetModels("codex");
        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.ModelId == "gpt-5.4" && m.IsAvailable);
    }

    [Fact]
    public async Task Refresher_Preserves_Operator_Tuned_Capability_Fields()
    {
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        // Operator-seeded entry with custom context + cost.
        catalog.Upsert(new ModelEntry("codex", "gpt-5.4", ModelTier.Code, "old desc", true, DateTimeOffset.UtcNow,
            MaxContextTokens: 2_000_000, InputPerMTokens: 12m, OutputPerMTokens: 48m));
        var prober = new StaticProber("codex", new ProbeResult("codex", true, new[]
        {
            new ProbedModel("gpt-5.4", "new description from probe", ModelTier.Code),
        }, null));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, catalog,
            new ModelCatalogRefresherOptions(),
            NullLogger<ModelCatalogRefresher>.Instance);

        await refresher.RefreshAllAsync();

        var entry = catalog.GetModels("codex").Single();
        Assert.Equal("new description from probe", entry.Description);
        Assert.Equal(2_000_000, entry.MaxContextTokens);   // preserved
        Assert.Equal(12m, entry.InputPerMTokens);          // preserved
        Assert.Equal(48m, entry.OutputPerMTokens);         // preserved
    }

    [Fact]
    public async Task Refresher_Prunes_Stale_Entries_By_Default()
    {
        // Default policy: if the probe succeeds and an existing entry is
        // no longer reported, it is REMOVED so the dropdown matches the CLI.
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        catalog.Upsert(new ModelEntry("codex", "gone",  ModelTier.Deep, "was available", true, DateTimeOffset.UtcNow));
        catalog.Upsert(new ModelEntry("codex", "still", ModelTier.Deep, "still around",  true, DateTimeOffset.UtcNow));

        var prober = new StaticProber("codex", new ProbeResult("codex", true, new[]
        {
            new ProbedModel("still", "still there", ModelTier.Deep),
        }, null));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, catalog,
            new ModelCatalogRefresherOptions(), // defaults: PruneStaleOnProbe = true
            NullLogger<ModelCatalogRefresher>.Instance);

        await refresher.RefreshAllAsync();

        var survivors = catalog.GetModels("codex");
        Assert.Single(survivors);
        Assert.Equal("still", survivors[0].ModelId);
    }

    [Fact]
    public async Task Refresher_Marks_Stale_Entries_Unavailable_When_Opted_Out_Of_Prune()
    {
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        catalog.Upsert(new ModelEntry("codex", "gone",  ModelTier.Deep, "was available", true, DateTimeOffset.UtcNow));
        catalog.Upsert(new ModelEntry("codex", "still", ModelTier.Deep, "still around",  true, DateTimeOffset.UtcNow));

        var prober = new StaticProber("codex", new ProbeResult("codex", true, new[]
        {
            new ProbedModel("still", "still there", ModelTier.Deep),
        }, null));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, catalog,
            new ModelCatalogRefresherOptions { PruneStaleOnProbe = false, MarkStaleAsUnavailable = true },
            NullLogger<ModelCatalogRefresher>.Instance);

        await refresher.RefreshAllAsync();

        var gone = catalog.GetModels("codex").Single(m => m.ModelId == "gone");
        var still = catalog.GetModels("codex").Single(m => m.ModelId == "still");
        Assert.False(gone.IsAvailable);
        Assert.True(still.IsAvailable);
    }

    [Fact]
    public async Task RefreshVendorAsync_Probes_Only_The_Named_Vendor()
    {
        // Two vendors registered. Refresh "codex" — only the codex prober
        // should fire; claude prober untouched.
        var codexProber  = new TrackingProber("codex", new ProbeResult("codex", true,
            new[] { new ProbedModel("gpt-5.4", "flagship", ModelTier.Deep) }, null));
        var claudeProber = new TrackingProber("claude", new ProbeResult("claude", true,
            new[] { new ProbedModel("claude-haiku-4-5", "fast", ModelTier.Fast) }, null));
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)codexProber, claudeProber }, catalog,
            new ModelCatalogRefresherOptions(),
            NullLogger<ModelCatalogRefresher>.Instance);

        var result = await refresher.RefreshVendorAsync("codex");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, codexProber.ProbeCalls);
        Assert.Equal(0, claudeProber.ProbeCalls);
        Assert.Single(catalog.GetModels("codex"));
        Assert.Empty(catalog.GetModels("claude"));
    }

    [Fact]
    public async Task RefreshVendorAsync_Debounces_Bursts_Within_Window()
    {
        // Two refreshes back-to-back should fire the prober exactly once;
        // the second is suppressed by the per-vendor debounce.
        var prober = new TrackingProber("codex", new ProbeResult("codex", true,
            new[] { new ProbedModel("gpt-5.4", "flagship", ModelTier.Deep) }, null));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, new ModelCatalog(seed: Array.Empty<ModelEntry>()),
            new ModelCatalogRefresherOptions(),
            NullLogger<ModelCatalogRefresher>.Instance);

        await refresher.RefreshVendorAsync("codex");
        await refresher.RefreshVendorAsync("codex");

        Assert.Equal(1, prober.ProbeCalls);
    }

    [Fact]
    public async Task RefreshVendorAsync_Returns_Null_For_Unknown_Vendor()
    {
        var refresher = new ModelCatalogRefresher(
            Array.Empty<IModelProber>(), new ModelCatalog(seed: Array.Empty<ModelEntry>()),
            new ModelCatalogRefresherOptions(),
            NullLogger<ModelCatalogRefresher>.Instance);

        Assert.Null(await refresher.RefreshVendorAsync("nonexistent"));
    }

    private sealed class TrackingProber : IModelProber
    {
        public string VendorId { get; }
        public int ProbeCalls { get; private set; }
        private readonly ProbeResult _result;
        public TrackingProber(string v, ProbeResult r) { VendorId = v; _result = r; }
        public Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
        {
            ProbeCalls++;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Composite_Prober_Preserves_Both_Raw_Outputs_When_Fallback_Wins()
    {
        // Primary fails with a captured stdout; fallback succeeds with its own
        // raw text. The composite should return the fallback result but carry
        // BOTH raw outputs concatenated so the /models/probes detail panel can
        // show what each route actually said.
        var primary = new StaticProber("codex", new ProbeResult(
            "codex", false, Array.Empty<ProbedModel>(),
            "no_models_parsed",
            RawOutput: "codex CLI banner\ncould not enter interactive mode",
            Source: "native-cli"));
        var fallback = new StaticProber("codex", new ProbeResult(
            "codex", true,
            new[] { new ProbedModel("gpt-5.4", "flagship", ModelTier.Deep) },
            null,
            RawOutput: "1. gpt-5.4  flagship",
            Source: "llm-fallback"));
        var composite = new FallbackCompositeProber(primary, fallback,
            NullLogger<FallbackCompositeProber>.Instance);

        var result = await composite.ProbeAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("llm-fallback", result.Source);
        Assert.NotNull(result.RawOutput);
        Assert.Contains("native-cli attempt", result.RawOutput!);
        Assert.Contains("llm-fallback attempt", result.RawOutput);
        Assert.Contains("codex CLI banner", result.RawOutput);
        Assert.Contains("1. gpt-5.4", result.RawOutput);
    }

    [Fact]
    public async Task Failed_Probe_Does_Not_Mutate_Catalog()
    {
        var catalog = new ModelCatalog(seed: Array.Empty<ModelEntry>());
        catalog.Upsert(new ModelEntry("codex", "keep", ModelTier.Deep, "", true, DateTimeOffset.UtcNow));
        var prober = new StaticProber("codex", new ProbeResult("codex", false, Array.Empty<ProbedModel>(), "timeout"));
        var refresher = new ModelCatalogRefresher(
            new[] { (IModelProber)prober }, catalog,
            new ModelCatalogRefresherOptions { MarkStaleAsUnavailable = true },
            NullLogger<ModelCatalogRefresher>.Instance);

        await refresher.RefreshAllAsync();

        var keep = catalog.GetModels("codex").Single();
        Assert.True(keep.IsAvailable);
    }

    [Fact]
    public void Default_Probe_Specs_Cover_All_Three_Vendors()
    {
        var specs = ModelProberDefaults.Defaults();
        Assert.Contains(specs, s => s.VendorId == "claude");
        Assert.Contains(specs, s => s.VendorId == "gemini");
        Assert.Contains(specs, s => s.VendorId == "codex");
    }
}
