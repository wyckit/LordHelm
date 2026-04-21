using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class LlmSwarmAggregatorTests
{
    private sealed class StubProviders : IProviderOrchestrator
    {
        public string? Response;
        public ErrorRecord? Error;
        public string? LastPrompt;

        public IReadOnlyList<string> VendorIds => new[] { "claude" };
        public Task<ProviderResponse> GenerateAsync(string _, string? __, string prompt, int ___ = 512, float ____ = 0.1f, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(new ProviderResponse(Response ?? "", Array.Empty<ToolCall>(), new UsageRecord(1, 1, 0), Error));
        }
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string prompt, int ___ = 512, float ____ = 0.1f, CancellationToken ct = default)
            => GenerateAsync(_, __, prompt, ___, ____, ct);
    }

    private static readonly TaskNode Node = new("t", "audit Foo.cs", Array.Empty<string>(),
        SwarmSize: 3, SwarmStrategy: SwarmStrategy.Diverse);

    private static readonly SwarmMemberOutput[] Members = new[]
    {
        new SwarmMemberOutput("t#m0", "code-auditor",    "claude", "Foo.cs:42 uses unchecked cast."),
        new SwarmMemberOutput("t#m1", "security-analyst","gemini", "Foo.cs:17 logs the auth token."),
        new SwarmMemberOutput("t#m2", "refactor-engineer","codex", "Extract parseArgs into helper."),
    };

    [Fact]
    public void Prompt_Includes_Persona_And_Vendor_Headers_For_Each_Member()
    {
        var prompt = LlmSwarmAggregator.BuildPrompt(Node, Members);
        Assert.Contains("persona: code-auditor", prompt);
        Assert.Contains("vendor: claude", prompt);
        Assert.Contains("persona: security-analyst", prompt);
        Assert.Contains("vendor: gemini", prompt);
        Assert.Contains("audit Foo.cs", prompt);
        Assert.Contains("Conflicts", prompt);
    }

    [Fact]
    public async Task Single_Member_Short_Circuits_Without_LLM_Call()
    {
        var stub = new StubProviders();
        var agg = new LlmSwarmAggregator(stub, NullLogger<LlmSwarmAggregator>.Instance);
        var result = await agg.AggregateAsync(Node, new[] { Members[0] });
        Assert.Equal(Members[0].Output, result);
        Assert.Null(stub.LastPrompt); // never called
    }

    [Fact]
    public async Task Merged_Output_Is_Returned_When_LLM_Succeeds()
    {
        var stub = new StubProviders { Response = "# Merged\n- A\n- B" };
        var agg = new LlmSwarmAggregator(stub, NullLogger<LlmSwarmAggregator>.Instance);
        var result = await agg.AggregateAsync(Node, Members);
        Assert.Equal("# Merged\n- A\n- B", result);
        Assert.NotNull(stub.LastPrompt);
        Assert.Contains("audit Foo.cs", stub.LastPrompt!);
    }

    [Fact]
    public async Task Fallback_To_Concat_On_Provider_Error()
    {
        var stub = new StubProviders { Error = new ErrorRecord("boom", "fail") };
        var agg = new LlmSwarmAggregator(stub, NullLogger<LlmSwarmAggregator>.Instance);
        var result = await agg.AggregateAsync(Node, Members);
        // Concat output mentions every persona
        Assert.Contains("code-auditor", result);
        Assert.Contains("security-analyst", result);
        Assert.Contains("refactor-engineer", result);
    }
}
