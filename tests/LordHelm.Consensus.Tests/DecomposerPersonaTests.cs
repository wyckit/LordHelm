using LordHelm.Core;
using LordHelm.Orchestrator;

namespace LordHelm.Consensus.Tests;

public class DecomposerPersonaTests
{
    [Fact]
    public void BuildPrompt_Lists_Persona_Ids_With_One_Liner()
    {
        var personas = ExpertDirectory.Default().All();
        var prompt = LlmGoalDecomposer.BuildPrompt("goal", Array.Empty<SkillManifest>(), personas, 10);
        Assert.Contains("PERSONA CATALOGUE", prompt);
        Assert.Contains("code-auditor", prompt);
        Assert.Contains("tech-writer", prompt);
        Assert.Contains("synthesiser", prompt);
        // Do NOT include full system hint — keep prompt compact
        Assert.DoesNotContain("You review code for correctness", prompt);
    }

    [Fact]
    public void BuildPrompt_Declares_Closed_Swarm_Strategy_Enum()
    {
        var prompt = LlmGoalDecomposer.BuildPrompt("goal", Array.Empty<SkillManifest>(), Array.Empty<ExpertPersona>(), 10);
        Assert.Contains("\"Single\"|\"Redundant\"|\"Diverse\"", prompt);
        Assert.Contains("swarmSize", prompt);
        Assert.Contains("tier", prompt);
    }

    [Fact]
    public void TryParse_Reads_Persona_Skill_Vendor_Swarm_Fields()
    {
        var raw = """
            {"tasks":[
              {"id":"scan","goal":"scan","dependsOn":[],"persona":"code-auditor","skill":"read-file","preferredVendor":"claude","swarmSize":3,"swarmStrategy":"Diverse"},
              {"id":"merge","goal":"merge","dependsOn":["scan"],"persona":"synthesiser","skill":null,"preferredVendor":null,"swarmSize":1,"swarmStrategy":"Single"}
            ]}
            """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "g", out var tasks, out var reason);
        Assert.True(ok, reason);
        Assert.Equal(2, tasks.Count);
        var scan = tasks.Single(t => t.Id == "scan");
        Assert.Equal("code-auditor", scan.Persona);
        Assert.Equal("read-file", scan.Skill);
        Assert.Equal("claude", scan.PreferredVendor);
        Assert.Equal(3, scan.SwarmSize);
        Assert.Equal(SwarmStrategy.Diverse, scan.SwarmStrategy);
        var merge = tasks.Single(t => t.Id == "merge");
        Assert.Equal("synthesiser", merge.Persona);
        Assert.Null(merge.Skill);
        Assert.Null(merge.PreferredVendor);
        Assert.Equal(1, merge.SwarmSize);
        Assert.Equal(SwarmStrategy.Single, merge.SwarmStrategy);
    }

    [Fact]
    public void TryParse_Rejects_Unknown_SwarmStrategy_But_Defaults_Safely()
    {
        // Council: unknown enum → default to Single rather than fail the parse,
        // because we want resilience to mild prompt drift.
        var raw = """
            {"tasks":[{"id":"x","goal":"y","dependsOn":[],"swarmStrategy":"fan-out"}]}
            """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "g", out var tasks, out _);
        Assert.True(ok);
        Assert.Equal(SwarmStrategy.Single, tasks[0].SwarmStrategy);
    }

    [Fact]
    public void TryParse_Clamps_SwarmSize_To_1_5()
    {
        var raw = """
            {"tasks":[{"id":"x","goal":"y","dependsOn":[],"swarmSize":99}]}
            """;
        var ok = LlmGoalDecomposer.TryParseTasks(raw, "g", out var tasks, out _);
        Assert.True(ok);
        Assert.Equal(5, tasks[0].SwarmSize);
    }
}
