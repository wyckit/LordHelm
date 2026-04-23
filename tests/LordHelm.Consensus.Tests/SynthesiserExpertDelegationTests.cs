using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class SynthesiserExpertDelegationTests
{
    private sealed class StubProviders : IProviderOrchestrator
    {
        public string? Response;
        public int ProviderCalls { get; private set; }
        public IReadOnlyList<string> VendorIds => new[] { "claude" };
        public Task<ProviderResponse> GenerateAsync(string _, string? __, string prompt, int ___ = 512, float ____ = 0.1f, CancellationToken ct = default)
        {
            ProviderCalls++;
            return Task.FromResult(new ProviderResponse(Response ?? "provider-output", Array.Empty<ToolCall>(), new UsageRecord(1, 1, 0), null));
        }
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string prompt, int ___ = 512, float ____ = 0.1f, CancellationToken ct = default)
            => GenerateAsync(_, __, prompt, ___, ____, ct);
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string prompt, ProviderTaskHint hint, int ___ = 512, float ____ = 0.1f, CancellationToken ct = default)
            => GenerateAsync(_, __, prompt, ___, ____, ct);
    }

    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public string Response { get; set; } = "expert-output";
        public int Calls { get; private set; }
        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temp, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<string?>(Response);
        }
        public Task<bool> IsAvailableAsync(string m, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private static (IExpertRegistry registry, FakeCli cli) BuildRegistry()
    {
        var cli = new FakeCli();
        var adapter = new ClaudeCodeAdapter(new ClaudeCliModelClient());
        // Use a proxy adapter whose CLI we control: wrap our FakeCli in a StubAdapter.
        var stub = new StubAdapter("claude", cli);
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { stub });
        var router = new AdapterRouter(registry);
        return (new ExpertRegistry(ExpertDirectory.Default(), router), cli);
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, IAgentOutcomeModelClient cli)
            : base(vendor, "claude-opus-4-7",
                new AdapterCapabilities(
                    new[] { "reasoning", "summarisation" }, 100_000, true, true, true, ResourceMode.Interactive,
                    new CostProfile(1m, 2m, 0m)),
                cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    [Fact]
    public async Task Synthesizer_Prefers_Expert_Over_Provider_When_Registered()
    {
        var (registry, cli) = BuildRegistry();
        var providers = new StubProviders();

        var synth = new LlmSynthesizer(providers, registry, NullLogger<LlmSynthesizer>.Instance);
        var req = new SynthesisRequest(
            Goal: "final answer",
            Dag: new[]
            {
                new TaskNode("a", "do a", Array.Empty<string>()),
                new TaskNode("b", "do b", new[] { "a" }),
            },
            NodeOutputs: new Dictionary<string, string> { ["a"] = "A-out", ["b"] = "B-out" });

        cli.Response = "merged-via-expert";
        var result = await synth.SynthesizeAsync(req);

        Assert.Equal("merged-via-expert", result);
        Assert.Equal(1, cli.Calls);
        Assert.Equal(0, providers.ProviderCalls);
    }

    [Fact]
    public async Task SwarmAggregator_Prefers_Expert_Over_Provider_When_Registered()
    {
        var (registry, cli) = BuildRegistry();
        var providers = new StubProviders();

        var agg = new LlmSwarmAggregator(providers, registry, NullLogger<LlmSwarmAggregator>.Instance);
        var node = new TaskNode("t", "audit", Array.Empty<string>(),
            SwarmSize: 2, SwarmStrategy: SwarmStrategy.Diverse);
        var members = new[]
        {
            new SwarmMemberOutput("t#m0", "code-auditor",     "claude", "found bug X"),
            new SwarmMemberOutput("t#m1", "security-analyst", "gemini", "found bug Y"),
        };

        cli.Response = "synth-via-expert";
        var result = await agg.AggregateAsync(node, members);

        Assert.Equal("synth-via-expert", result);
        Assert.Equal(1, cli.Calls);
        Assert.Equal(0, providers.ProviderCalls);
    }

    [Fact]
    public async Task Synthesizer_Falls_Through_To_Provider_When_Expert_Fails()
    {
        var (registry, cli) = BuildRegistry();
        var providers = new StubProviders { Response = "provider-merge" };
        // Make the adapter return null → expert fails, falls through.
        cli.Response = null!;

        var synth = new LlmSynthesizer(providers, registry, NullLogger<LlmSynthesizer>.Instance);
        var req = new SynthesisRequest(
            Goal: "x",
            Dag: new[]
            {
                new TaskNode("a", "a", Array.Empty<string>()),
                new TaskNode("b", "b", new[] { "a" }),
            },
            NodeOutputs: new Dictionary<string, string> { ["a"] = "A", ["b"] = "B" });

        var result = await synth.SynthesizeAsync(req);
        Assert.Equal("provider-merge", result);
        Assert.Equal(1, providers.ProviderCalls);
    }
}
