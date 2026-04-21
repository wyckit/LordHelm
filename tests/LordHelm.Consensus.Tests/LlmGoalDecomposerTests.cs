using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class LlmGoalDecomposerTests
{
    private sealed class StubProviders : IProviderOrchestrator
    {
        private readonly string _response;
        private readonly ErrorRecord? _error;
        public StubProviders(string response, ErrorRecord? error = null) { _response = response; _error = error; }
        public IReadOnlyList<string> VendorIds => new[] { "claude" };
        public Task<ProviderResponse> GenerateAsync(string _, string? __, string ___, int ____ = 512, float _____ = 0.1f, CancellationToken ct = default) =>
            Task.FromResult(new ProviderResponse(_response, Array.Empty<ToolCall>(), new UsageRecord(1, 1, 0), _error));
        public Task<ProviderResponse> GenerateWithFailoverAsync(string _, string? __, string ___, int ____ = 512, float _____ = 0.1f, CancellationToken ct = default) =>
            GenerateAsync(_, __, ___, ____, _____, ct);
    }

    [Fact]
    public async Task Well_Formed_Json_Parses_Into_Multi_Node_Dag()
    {
        var response = """
            {"tasks":[
              {"id":"scan","goal":"scan files","dependsOn":[]},
              {"id":"report","goal":"write markdown","dependsOn":["scan"]}
            ]}
            """;
        var d = new LlmGoalDecomposer(new StubProviders(response),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("audit repo", Array.Empty<SkillManifest>());
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.Id == "scan");
        Assert.Contains(tasks, t => t.Id == "report" && t.DependsOn.Contains("scan"));
    }

    [Fact]
    public async Task Markdown_Fenced_Json_Still_Parses()
    {
        var response = "```json\n{\"tasks\":[{\"id\":\"a\",\"goal\":\"x\",\"dependsOn\":[]}]}\n```";
        var d = new LlmGoalDecomposer(new StubProviders(response),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("g", Array.Empty<SkillManifest>());
        Assert.Single(tasks);
        Assert.Equal("a", tasks[0].Id);
    }

    [Fact]
    public async Task Unparseable_Response_Falls_Back_To_Passthrough()
    {
        var d = new LlmGoalDecomposer(new StubProviders("sorry, no json here"),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("do the thing", Array.Empty<SkillManifest>());
        Assert.Single(tasks);
        Assert.Equal("root", tasks[0].Id);
        Assert.Equal("do the thing", tasks[0].Goal);
    }

    [Fact]
    public async Task Provider_Error_Falls_Back_To_Passthrough()
    {
        var d = new LlmGoalDecomposer(new StubProviders("", new ErrorRecord("boom", "x")),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("g", Array.Empty<SkillManifest>());
        Assert.Single(tasks);
        Assert.Equal("root", tasks[0].Id);
    }

    [Fact]
    public async Task Cyclic_Dag_Falls_Back()
    {
        var response = """{"tasks":[{"id":"a","goal":"","dependsOn":["b"]},{"id":"b","goal":"","dependsOn":["a"]}]}""";
        var d = new LlmGoalDecomposer(new StubProviders(response),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("g", Array.Empty<SkillManifest>());
        Assert.Single(tasks);
        Assert.Equal("root", tasks[0].Id);
    }

    [Fact]
    public async Task Dangling_Dependency_Falls_Back()
    {
        var response = """{"tasks":[{"id":"a","goal":"","dependsOn":["ghost"]}]}""";
        var d = new LlmGoalDecomposer(new StubProviders(response),
            new LlmDecomposerOptions(), NullLogger<LlmGoalDecomposer>.Instance);
        var tasks = await d.DecomposeAsync("g", Array.Empty<SkillManifest>());
        Assert.Single(tasks);
        Assert.Equal("root", tasks[0].Id);
    }
}
