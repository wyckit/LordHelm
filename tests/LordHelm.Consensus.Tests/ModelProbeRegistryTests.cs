using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.ModelDiscovery;
using LordHelm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ModelProbeRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ModelProbeRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helm-probes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "model-probes.json");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public void Registry_Seeds_From_Defaults()
    {
        var reg = new ModelProbeRegistry();
        Assert.Equal(3, reg.All.Count);
        Assert.NotNull(reg.Get("claude"));
        Assert.NotNull(reg.Get("codex"));
    }

    [Fact]
    public void Upsert_Replaces_Existing_Spec_And_Fires_OnChanged()
    {
        var reg = new ModelProbeRegistry();
        var hits = 0;
        reg.OnChanged += () => hits++;

        reg.Upsert(new ModelProbeSpec("codex", "codex-cli", new[] { "--list-models" }));
        Assert.Equal(1, hits);
        Assert.Equal("codex-cli", reg.Get("codex")!.Executable);
    }

    [Fact]
    public async Task Roundtrip_Via_JsonFile_Store()
    {
        var reg = new ModelProbeRegistry();
        reg.Upsert(new ModelProbeSpec("claude", "claude", new[] { "--help" },
            StdinInput: "/model\n", Timeout: TimeSpan.FromSeconds(5)));

        var store = new JsonFileModelProbeConfigStore(_path, NullLogger<JsonFileModelProbeConfigStore>.Instance);
        await store.SaveAsync(reg);

        var reg2 = new ModelProbeRegistry(seed: Array.Empty<ModelProbeSpec>());
        await store.LoadAsync(reg2);

        var claude = reg2.Get("claude")!;
        Assert.Equal("/model\n", claude.StdinInput);
        Assert.Equal(5, (int)claude.Timeout!.Value.TotalSeconds);
    }

    private sealed class StaticProber : IModelProber
    {
        public string VendorId { get; }
        private readonly ProbeResult _result;
        public StaticProber(string v, ProbeResult r) { VendorId = v; _result = r; }
        public Task<ProbeResult> ProbeAsync(CancellationToken ct = default) => Task.FromResult(_result);
    }

    [Fact]
    public async Task FallbackCompositeProber_Uses_Primary_On_Success()
    {
        var primary = new StaticProber("codex", new ProbeResult("codex", true, new[] { new ProbedModel("gpt-5.4", "d", ModelTier.Code) }, null));
        var fallback = new StaticProber("codex", new ProbeResult("codex", true, new[] { new ProbedModel("from-fallback", "d", ModelTier.Deep) }, null));
        var composed = new FallbackCompositeProber(primary, fallback, NullLogger<FallbackCompositeProber>.Instance);

        var r = await composed.ProbeAsync();

        Assert.True(r.Succeeded);
        Assert.Equal("gpt-5.4", r.Models[0].ModelId);
    }

    [Fact]
    public async Task FallbackCompositeProber_Uses_Fallback_When_Primary_Fails()
    {
        var primary = new StaticProber("codex", new ProbeResult("codex", false, Array.Empty<ProbedModel>(), "timeout"));
        var fallback = new StaticProber("codex", new ProbeResult("codex", true, new[] { new ProbedModel("llm-discovered", "via llm", ModelTier.Code) }, null));
        var composed = new FallbackCompositeProber(primary, fallback, NullLogger<FallbackCompositeProber>.Instance);

        var r = await composed.ProbeAsync();

        Assert.True(r.Succeeded);
        Assert.Equal("llm-discovered", r.Models[0].ModelId);
    }

    private sealed class StubProviders : IProviderOrchestrator
    {
        public string Response { get; set; } = "";
        public IReadOnlyList<string> VendorIds => new[] { "claude" };
        public Task<ProviderResponse> GenerateAsync(string _, string? __, string ___, int ____ = 512, float _____ = 0.1f, CancellationToken ct = default) =>
            Task.FromResult(new ProviderResponse(Response, Array.Empty<ToolCall>(), new UsageRecord(1, 1, 0), null));
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string ___, int ____ = 512, float _____ = 0.1f, CancellationToken ct = default) =>
            GenerateAsync(_, __, ___, ____, _____, ct);
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string ___, ProviderTaskHint hint, int ____ = 512, float _____ = 0.1f, CancellationToken ct = default) =>
            GenerateAsync(_, __, ___, ____, _____, ct);
    }

    [Fact]
    public async Task LlmFallbackProber_Parses_Models_From_LLM_Response()
    {
        var providers = new StubProviders
        {
            Response = "1. claude-opus-4-7  deep reasoning\n2. claude-haiku-4-5  fast tier\n"
        };
        var prober = new LlmFallbackProber("claude", providers, new NumberedListModelParser(),
            NullLogger<LlmFallbackProber>.Instance);

        var r = await prober.ProbeAsync();

        Assert.True(r.Succeeded);
        Assert.Equal(2, r.Models.Count);
        Assert.Equal("claude-opus-4-7", r.Models[0].ModelId);
    }
}
