using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

public class AdapterProviderOrchestratorTests
{
    private sealed class FakeCli : IAgentOutcomeModelClient
    {
        private readonly Queue<Func<string?>> _responses = new();
        public int Calls { get; private set; }
        public FakeCli Returns(string? value) { _responses.Enqueue(() => value); return this; }
        public FakeCli Throws(Exception ex) { _responses.Enqueue(() => throw ex); return this; }
        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens, float temperature, CancellationToken ct)
        {
            Calls++;
            if (_responses.Count == 0) return Task.FromResult<string?>("default");
            return Task.FromResult(_responses.Dequeue()());
        }
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class StubAdapter : AdapterBase
    {
        public StubAdapter(string vendor, IAgentOutcomeModelClient cli)
            : base(vendor, "m1",
                new AdapterCapabilities(
                    new[] { "code" }, 100_000, true, true, true, ResourceMode.Interactive,
                    new CostProfile(1m, 2m, 0m)),
                cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    private static (AdapterProviderOrchestrator orch, FakeCli cliA, FakeCli cliB) Build()
    {
        var cliA = new FakeCli();
        var cliB = new FakeCli();
        var a = new StubAdapter("a", cliA);
        var b = new StubAdapter("b", cliB);
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { a, b });
        var router = new AdapterRouter(registry);
        return (new AdapterProviderOrchestrator(registry, router), cliA, cliB);
    }

    [Fact]
    public async Task GenerateAsync_Dispatches_To_Named_Vendor()
    {
        var (orch, cliA, cliB) = Build();
        cliA.Returns("from-a");

        var resp = await orch.GenerateAsync("a", null, "hi");

        Assert.Null(resp.Error);
        Assert.Equal("from-a", resp.AssistantMessage);
        Assert.Equal(1, cliA.Calls);
        Assert.Equal(0, cliB.Calls);
    }

    [Fact]
    public async Task GenerateAsync_Unknown_Vendor_Returns_Error()
    {
        var (orch, _, _) = Build();

        var resp = await orch.GenerateAsync("nope", null, "hi");

        Assert.NotNull(resp.Error);
        Assert.Equal("unknown_vendor", resp.Error!.Code);
    }

    [Fact]
    public async Task GenerateWithFailover_Falls_Through_To_Next_Adapter()
    {
        var (orch, cliA, cliB) = Build();
        cliA.Throws(new InvalidOperationException("boom"));
        cliB.Returns("from-b");

        var resp = await orch.GenerateWithFailoverAsync("a", null, "hi");

        Assert.Null(resp.Error);
        Assert.Equal("from-b", resp.AssistantMessage);
        Assert.Equal(1, cliA.Calls);
        Assert.Equal(1, cliB.Calls);
    }

    [Fact]
    public async Task GenerateWithFailover_With_Hint_Passes_TaskKind_Through_To_Router()
    {
        // Build two adapters with mutually exclusive task kinds. Pin the first to
        // fail; the hint then must route the fallback to the adapter whose
        // capabilities match the stated task-kind.
        var cliA = new FakeCli().Throws(new InvalidOperationException("boom"));
        var cliB = new FakeCli().Returns("from-b");
        var a = new StubAdapter("a", cliA);

        // Replace b's capabilities with something only "research" matches.
        var researchCaps = new AdapterCapabilities(
            new[] { "research" }, 100_000, true, true, true, ResourceMode.Batch,
            new CostProfile(1m, 2m, 0m));
        IAgentModelAdapter b = new CapOverrideAdapter("b", researchCaps, cliB);
        var registry = new AgentAdapterRegistry(new[] { (IAgentModelAdapter)a, b });
        var router = new AdapterRouter(registry);
        var orch = new AdapterProviderOrchestrator(registry, router);

        var hint = new ProviderTaskHint(TaskKind: "research", PreferredMode: ResourceMode.Batch);
        var resp = await orch.GenerateWithFailoverAsync("a", null, "hi", hint);

        Assert.Null(resp.Error);
        Assert.Equal("from-b", resp.AssistantMessage);
    }

    private sealed class CapOverrideAdapter : AdapterBase
    {
        public CapOverrideAdapter(string vendor, AdapterCapabilities caps, IAgentOutcomeModelClient cli)
            : base(vendor, "m1", caps, cli, new RateLimitGovernor(10, TimeSpan.FromMinutes(1))) { }
    }

    [Fact]
    public void Health_Surface_Reflects_Registry()
    {
        var (orch, _, _) = Build();
        var all = orch.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.VendorId == "a");
        Assert.Contains(all, v => v.VendorId == "b");
        Assert.NotNull(orch.Get("a"));
        Assert.Null(orch.Get("missing"));
    }
}
