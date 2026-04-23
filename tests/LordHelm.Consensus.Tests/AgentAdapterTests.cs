using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class AgentAdapterTests
{
    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        public string? Next { get; set; } = "ok";
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public string? LastModel { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temperature, CancellationToken ct)
        {
            Calls++;
            LastModel = model;
            LastPrompt = prompt;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Next);
        }

        public Task<bool> IsAvailableAsync(string model, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class TestAdapter : AdapterBase
    {
        public TestAdapter(IAgentOutcomeModelClient cli, RateLimitGovernor gov)
            : base("test", "m1",
                new AdapterCapabilities(
                    new[] { "t" }, 8000, true, true, true,
                    ResourceMode.Interactive,
                    new CostProfile(1m, 2m, 0m)),
                cli, gov) { }
    }

    [Fact]
    public async Task GenerateAsync_Returns_Normalised_Success()
    {
        var cli = new FakeCli { Next = "hello world" };
        var adapter = new TestAdapter(cli, new RateLimitGovernor(5, TimeSpan.FromMinutes(1)));

        var r = await adapter.GenerateAsync(new AgentRequest("hi", SystemHint: "be brief"));

        Assert.Null(r.Response.Error);
        Assert.Equal("hello world", r.Response.AssistantMessage);
        Assert.Equal("test", r.VendorId);
        Assert.Equal("m1", r.ModelId);
        Assert.Equal("be brief\n\nhi", cli.LastPrompt);
        Assert.True(adapter.Health.IsHealthy);
    }

    [Fact]
    public async Task GenerateAsync_Reports_NullResponse_Error()
    {
        var cli = new FakeCli { Next = null };
        var adapter = new TestAdapter(cli, new RateLimitGovernor(5, TimeSpan.FromMinutes(1)));

        var r = await adapter.GenerateAsync(new AgentRequest("hi"));

        Assert.NotNull(r.Response.Error);
        Assert.Equal("null_response", r.Response.Error!.Code);
        Assert.False(adapter.Health.IsHealthy);
        Assert.Equal("null_response", adapter.Health.LastError);
    }

    [Fact]
    public async Task GenerateAsync_Catches_Exception_And_Marks_Unhealthy()
    {
        var cli = new FakeCli { Throw = new InvalidOperationException("boom") };
        var adapter = new TestAdapter(cli, new RateLimitGovernor(5, TimeSpan.FromMinutes(1)));

        var r = await adapter.GenerateAsync(new AgentRequest("hi"));

        Assert.Equal("exception", r.Response.Error!.Code);
        Assert.Contains("boom", r.Response.Error.Message);
        Assert.False(adapter.Health.IsHealthy);
    }

    [Fact]
    public async Task GenerateAsync_Honours_ModelOverride()
    {
        var cli = new FakeCli();
        var adapter = new TestAdapter(cli, new RateLimitGovernor(5, TimeSpan.FromMinutes(1)));

        await adapter.GenerateAsync(new AgentRequest("x", ModelOverride: "m-override"));

        Assert.Equal("m-override", cli.LastModel);
    }

    [Fact]
    public void Registry_Indexes_Adapters_By_Vendor_Id()
    {
        var a = new TestAdapter(new FakeCli(), new RateLimitGovernor(5, TimeSpan.FromMinutes(1)));
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { a });

        Assert.Same(a, registry.Get("test"));
        Assert.Same(a, registry.Get("TEST"));
        Assert.Null(registry.Get("missing"));
        Assert.Single(registry.All);
    }

    [Fact]
    public void RoutingScore_Weights_Sum_To_One_And_Total_Matches_Design()
    {
        var s = new AdapterRoutingScore("v", 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0);
        Assert.Equal(1.0, s.Total, 3);

        var partial = new AdapterRoutingScore("v", 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        Assert.Equal(0.30, partial.Total, 3);
    }
}
