using System.Collections.Concurrent;
using LordHelm.Orchestrator;

namespace LordHelm.Consensus.Tests;

public class ThinkTankTests
{
    // ------------------------------- waves -------------------------------

    [Fact]
    public void ComputeWaves_Orders_Dependencies_Into_Parallel_Waves()
    {
        var nodes = new[]
        {
            new TaskNode("a", "", Array.Empty<string>()),
            new TaskNode("b", "", Array.Empty<string>()),
            new TaskNode("c", "", new[] { "a" }),
            new TaskNode("d", "", new[] { "a", "b" }),
            new TaskNode("e", "", new[] { "c", "d" }),
        };

        var waves = TaskDag.ComputeWaves(nodes);

        Assert.Equal(3, waves.Count);
        Assert.Equal(new[] { "a", "b" }, waves[0].Select(n => n.Id).OrderBy(x => x));
        Assert.Equal(new[] { "c", "d" }, waves[1].Select(n => n.Id).OrderBy(x => x));
        Assert.Equal(new[] { "e" }, waves[2].Select(n => n.Id));
    }

    [Fact]
    public void ComputeWaves_Detects_Cycles()
    {
        var nodes = new[]
        {
            new TaskNode("a", "", new[] { "b" }),
            new TaskNode("b", "", new[] { "a" }),
        };
        Assert.Throws<InvalidOperationException>(() => TaskDag.ComputeWaves(nodes));
    }

    // ------------------------------- swarm -------------------------------

    [Fact]
    public async Task Swarm_Aggregator_Merges_N_Outputs()
    {
        var agg = new ConcatSwarmAggregator();
        var task = new TaskNode("t", "analyse repo", Array.Empty<string>(),
            SwarmSize: 3, SwarmStrategy: SwarmStrategy.Diverse);
        var members = new[]
        {
            new SwarmMemberOutput("t#m0", "code-auditor",    "claude", "No issues found."),
            new SwarmMemberOutput("t#m1", "security-analyst","gemini", "Missing input validation at line 42."),
            new SwarmMemberOutput("t#m2", "refactor-engineer","codex", "Consider extracting parseArgs() into its own method."),
        };
        var merged = await agg.AggregateAsync(task, members);
        Assert.Contains("code-auditor", merged);
        Assert.Contains("security-analyst", merged);
        Assert.Contains("refactor-engineer", merged);
        Assert.Contains("No issues found.", merged);
        Assert.Contains("Missing input validation", merged);
    }

    // ------------------------------- directory -------------------------------

    [Fact]
    public void Directory_Default_Has_Core_Personas()
    {
        var dir = ExpertDirectory.Default();
        Assert.NotNull(dir.Get("code-auditor"));
        Assert.NotNull(dir.Get("tech-writer"));
        Assert.NotNull(dir.Get("security-analyst"));
        Assert.NotNull(dir.Get("synthesiser"));
    }

    [Fact]
    public void Persona_Spawn_Instructions_Bind_The_Goal()
    {
        var p = ExpertDirectory.Default().Get("code-auditor")!;
        var text = p.SpawnInstructions("audit Foo.cs");
        Assert.Contains("Code Auditor", text);
        Assert.Contains("audit Foo.cs", text);
    }

    // ------------------------------- synthesiser fallback -------------------------------

    [Fact]
    public void Synthesis_Fallback_Joins_Every_Node_Output()
    {
        var req = new SynthesisRequest(
            "summarise readme",
            Dag: new[]
            {
                new TaskNode("scan", "scan readme", Array.Empty<string>()),
                new TaskNode("report", "write summary", new[] { "scan" }),
            },
            NodeOutputs: new Dictionary<string, string>
            {
                ["scan"] = "found 12 headings",
                ["report"] = "readme covers install, run, test",
            });

        var text = LlmSynthesizer.FallbackConcat(req);
        Assert.Contains("summarise readme", text);
        Assert.Contains("found 12 headings", text);
        Assert.Contains("readme covers install", text);
    }

    [Fact]
    public void Synthesis_Prompt_Includes_Persona_Hints()
    {
        var req = new SynthesisRequest(
            "audit",
            Dag: new[] { new TaskNode("a", "audit file", Array.Empty<string>(), Persona: "code-auditor") },
            NodeOutputs: new Dictionary<string, string> { ["a"] = "ok" });
        var prompt = LlmSynthesizer.BuildPrompt(req);
        Assert.Contains("persona: code-auditor", prompt);
        Assert.Contains("audit file", prompt);
    }

    // ------------------------------- manager parallel execution -------------------------------

    [Fact]
    public async Task Manager_Runs_Independent_Tasks_In_Parallel_Within_A_Wave()
    {
        var decomposer = new FixedDecomposer(new[]
        {
            new TaskNode("a", "", Array.Empty<string>()),
            new TaskNode("b", "", Array.Empty<string>()),
            new TaskNode("c", "", new[] { "a", "b" }),
        });

        var concurrent = new ConcurrentDictionary<string, int>();
        int maxConcurrency = 0;
        int active = 0;

        TaskExecutor exec = async (node, memberIndex, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            maxConcurrency = Math.Max(maxConcurrency, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref active);
            concurrent[node.Id] = memberIndex;
            return node.Id + "-done";
        };

        var manager = new LordHelmManager(decomposer, new ConcatSwarmAggregator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LordHelmManager>.Instance);
        var result = await manager.RunAsync("g", Array.Empty<LordHelm.Core.SkillManifest>(), exec);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.NodeOutputs.Count);
        Assert.True(maxConcurrency >= 2, $"expected wave 0 to run both a+b in parallel; saw maxConcurrency={maxConcurrency}");
    }

    [Fact]
    public async Task Manager_Fans_Out_Swarm_Tasks_To_N_Members()
    {
        var decomposer = new FixedDecomposer(new[]
        {
            new TaskNode("analyse", "review repo", Array.Empty<string>(),
                SwarmSize: 3, SwarmStrategy: SwarmStrategy.Diverse, Persona: "code-auditor"),
        });

        var members = new ConcurrentBag<int>();
        TaskExecutor exec = async (node, memberIndex, ct) =>
        {
            members.Add(memberIndex);
            await Task.Yield();
            return $"member-{memberIndex}";
        };

        var manager = new LordHelmManager(decomposer, new ConcatSwarmAggregator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LordHelmManager>.Instance);
        var result = await manager.RunAsync("g", Array.Empty<LordHelm.Core.SkillManifest>(), exec);

        Assert.True(result.Succeeded);
        Assert.Equal(3, members.Count);
        Assert.Equal(new[] { 0, 1, 2 }, members.OrderBy(i => i));
        var merged = result.NodeOutputs["analyse"];
        Assert.Contains("member-0", merged);
        Assert.Contains("member-1", merged);
        Assert.Contains("member-2", merged);
    }

    private sealed class FixedDecomposer : IGoalDecomposer
    {
        private readonly IReadOnlyList<TaskNode> _nodes;
        public FixedDecomposer(IReadOnlyList<TaskNode> nodes) { _nodes = nodes; }
        public Task<IReadOnlyList<TaskNode>> DecomposeAsync(string goal, IReadOnlyList<LordHelm.Core.SkillManifest> skills, CancellationToken ct = default)
            => Task.FromResult(_nodes);
    }
}
