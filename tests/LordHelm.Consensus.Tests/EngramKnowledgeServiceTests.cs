using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Knowledge;
using LordHelm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class EngramKnowledgeServiceTests
{
    private sealed class FakeEngram : IEngramClient
    {
        public List<(string ns, string id, string text, string? category, IReadOnlyDictionary<string, string>? meta)> Stores { get; } = new();
        public List<EngramHit> Hits { get; set; } = new();

        public Task StoreAsync(string @namespace, string id, string text,
            string? category = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            Stores.Add((@namespace, id, text, category, metadata));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EngramHit>> SearchAsync(string ns, string text, int k = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EngramHit>>(Hits);
        public Task<EngramHit?> GetAsync(string ns, string id, CancellationToken ct = default) => Task.FromResult<EngramHit?>(null);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeOrchestrator : IProviderOrchestrator
    {
        public ProviderResponse NextResponse { get; set; } = new(
            "Fresh research payload.", Array.Empty<ToolCall>(),
            new UsageRecord(10, 20, 0), null);
        public List<(string vendor, string? model, string prompt)> Calls { get; } = new();

        public IReadOnlyList<string> VendorIds => new[] { "claude", "gemini", "codex" };
        public Task<ProviderResponse> GenerateAsync(string v, string? m, string p, int mt = 512, float t = 0.1f, CancellationToken ct = default)
        {
            Calls.Add((v, m, p));
            return Task.FromResult(NextResponse);
        }
        public Task<ProviderResponse> GenerateWithFailoverAsync(string v, string? m, string p, int mt = 512, float t = 0.1f, CancellationToken ct = default)
            => GenerateAsync(v, m, p, mt, t, ct);
        public Task<ProviderResponse> GenerateWithFailoverAsync(string v, string? m, string p, ProviderTaskHint h, int mt = 512, float t = 0.1f, CancellationToken ct = default)
            => GenerateAsync(v, m, p, mt, t, ct);
    }

    private static EngramKnowledgeService NewService(FakeEngram e, FakeOrchestrator o, HelmPreferenceState? pref = null, KnowledgeOptions? opt = null)
    {
        pref ??= new HelmPreferenceState();
        return new EngramKnowledgeService(e, o, pref,
            NullLogger<EngramKnowledgeService>.Instance, opt);
    }

    [Fact]
    public async Task Recalls_From_Engram_When_Top_Hit_Meets_Threshold()
    {
        var engram = new FakeEngram
        {
            Hits = new List<EngramHit>
            {
                new(EngramKnowledgeService.Namespace, "knowledge-foo-202604220000",
                    "foo is a widget used in bar contexts", 0.88,
                    new Dictionary<string, string>())
            }
        };
        var providers = new FakeOrchestrator();
        var svc = NewService(engram, providers);

        var res = await svc.RecallOrResearchAsync("foo widgets");

        Assert.Equal(KnowledgeSource.Engram, res.Source);
        Assert.Empty(providers.Calls);
        Assert.Empty(engram.Stores);
        Assert.Contains("foo is a widget", res.Answer);
    }

    [Fact]
    public async Task Falls_Back_To_Research_When_Recall_Is_Weak_And_Persists_Result()
    {
        var engram = new FakeEngram(); // no hits
        var providers = new FakeOrchestrator
        {
            NextResponse = new ProviderResponse(
                "## Summary\nDetailed briefing.", Array.Empty<ToolCall>(),
                new UsageRecord(50, 200, 0), null)
        };
        var pref = new HelmPreferenceState();
        pref.SetPrimary("gemini", model: "gemini-2.5-pro", tier: "Fast");
        var svc = NewService(engram, providers, pref);

        var res = await svc.RecallOrResearchAsync("atomic clocks");

        Assert.Equal(KnowledgeSource.Research, res.Source);
        Assert.Equal("gemini", res.VendorUsed);
        Assert.Equal("gemini-2.5-pro", res.ModelUsed);
        Assert.Contains("Detailed briefing", res.Answer);

        Assert.Single(providers.Calls);
        Assert.Equal("gemini", providers.Calls[0].vendor);

        var stored = Assert.Single(engram.Stores);
        Assert.Equal(EngramKnowledgeService.Namespace, stored.ns);
        Assert.NotNull(stored.meta);
        Assert.Equal("atomic clocks", stored.meta!["topic"]);
        Assert.Equal("gemini", stored.meta["vendor"]);
        Assert.Equal(stored.id, res.StoredNodeId);
    }

    [Fact]
    public async Task Returns_Error_Result_When_Research_Call_Errors()
    {
        var engram = new FakeEngram();
        var providers = new FakeOrchestrator
        {
            NextResponse = new ProviderResponse(
                "", Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0),
                new ErrorRecord("quota", "exhausted"))
        };
        var svc = NewService(engram, providers);

        var res = await svc.RecallOrResearchAsync("anything");

        Assert.Equal(KnowledgeSource.Error, res.Source);
        Assert.Null(res.StoredNodeId);
        Assert.Empty(engram.Stores);
    }

    [Fact]
    public async Task Weak_Hits_Still_Trigger_Research_Below_Threshold()
    {
        var engram = new FakeEngram
        {
            Hits = new List<EngramHit>
            {
                new(EngramKnowledgeService.Namespace, "stale-note",
                    "tangentially related", 0.20,
                    new Dictionary<string, string>())
            }
        };
        var providers = new FakeOrchestrator();
        var svc = NewService(engram, providers);

        var res = await svc.RecallOrResearchAsync("deep topic");

        Assert.Equal(KnowledgeSource.Research, res.Source);
        Assert.Single(providers.Calls);
        Assert.Single(engram.Stores);
    }

    [Fact]
    public async Task Blank_Topic_Short_Circuits_With_Error()
    {
        var engram = new FakeEngram();
        var providers = new FakeOrchestrator();
        var svc = NewService(engram, providers);

        var res = await svc.RecallOrResearchAsync("   ");

        Assert.Equal(KnowledgeSource.Error, res.Source);
        Assert.Empty(providers.Calls);
        Assert.Empty(engram.Stores);
    }
}
