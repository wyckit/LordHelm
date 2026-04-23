using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class CostTrackingTests
{
    private sealed class FixedOutputCli : IAgentOutcomeModelClient
    {
        public string Response { get; set; } = new string('x', 4000); // ~1000 output tokens
        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temp, CancellationToken ct)
            => Task.FromResult<string?>(Response);
        public Task<bool> IsAvailableAsync(string m, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class CostAdapter : AdapterBase
    {
        public CostAdapter(IAgentOutcomeModelClient cli, decimal inPerM, decimal outPerM)
            : base("test", "m1",
                new AdapterCapabilities(
                    new[] { "code" }, 100_000, true, true, true, ResourceMode.Interactive,
                    new CostProfile(inPerM, outPerM, 0m)),
                cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    [Fact]
    public async Task GenerateAsync_Accumulates_Rolling_Cost()
    {
        // input $10/M, output $20/M, ~1000 output tokens per call, ~250 input tokens.
        var cli = new FixedOutputCli();
        var a = new CostAdapter(cli, inPerM: 10m, outPerM: 20m);

        Assert.Equal(0m, a.Health.RollingCostUsd);
        Assert.Equal(0, a.Health.RollingCallCount);

        await a.GenerateAsync(new AgentRequest(new string('p', 1000)));
        await a.GenerateAsync(new AgentRequest(new string('p', 1000)));

        var h = a.Health;
        Assert.Equal(2, h.RollingCallCount);
        Assert.True(h.RollingCostUsd > 0m);
        // 2 calls × (~250 tokens × 10 / 1M + ~1000 tokens × 20 / 1M)
        // ≈ 2 × (0.0025 + 0.020) = 0.045 — allow generous slack.
        Assert.True(h.RollingCostUsd < 0.10m,
            $"expected bounded cost, got {h.RollingCostUsd}");
    }

    [Fact]
    public async Task Failed_Call_Does_Not_Accumulate_Cost()
    {
        var cli = new FixedOutputCli { Response = null! };
        var a = new CostAdapter(cli, inPerM: 10m, outPerM: 20m);

        await a.GenerateAsync(new AgentRequest("hi"));
        Assert.Equal(0m, a.Health.RollingCostUsd);
        Assert.Equal(0, a.Health.RollingCallCount);
    }

    [Fact]
    public async Task Zero_Cost_Profile_Records_Zero_Spend()
    {
        var cli = new FixedOutputCli();
        var a = new CostAdapter(cli, inPerM: 0m, outPerM: 0m);

        await a.GenerateAsync(new AgentRequest("hi"));
        Assert.Equal(0m, a.Health.RollingCostUsd);
        Assert.Equal(0, a.Health.RollingCallCount);
    }
}
