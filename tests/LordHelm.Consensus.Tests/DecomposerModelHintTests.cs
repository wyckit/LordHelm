using LordHelm.Core;
using LordHelm.Orchestrator;

namespace LordHelm.Consensus.Tests;

public class DecomposerModelHintTests
{
    [Fact]
    public void ModelHint_Flows_From_Decomposer_Json_To_TaskNode()
    {
        var raw = """
            {"tasks":[
              {"id":"quick-audit",   "goal":"quick",          "dependsOn":[], "tier":"fast", "modelHint":"claude-haiku-4-5"},
              {"id":"deep-analysis", "goal":"deep",           "dependsOn":[], "tier":"deep"}
            ]}
            """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "x", out var tasks, out _);
        Assert.True(ok);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("claude-haiku-4-5", tasks[0].ModelHint);
        Assert.Null(tasks[1].ModelHint);
    }

    [Fact]
    public void Missing_ModelHint_Field_Is_Null()
    {
        var raw = """ {"tasks":[{"id":"t","goal":"g","dependsOn":[]}]} """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "x", out var tasks, out _);
        Assert.True(ok);
        Assert.Null(tasks[0].ModelHint);
    }

    [Fact]
    public void Empty_String_ModelHint_Treated_As_Null()
    {
        var raw = """ {"tasks":[{"id":"t","goal":"g","dependsOn":[], "modelHint": ""}]} """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "x", out var tasks, out _);
        Assert.True(ok);
        Assert.Null(tasks[0].ModelHint);
    }
}
