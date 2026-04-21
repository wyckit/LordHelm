using LordHelm.Core;
using LordHelm.Orchestrator;

namespace LordHelm.Consensus.Tests;

public class GoalRunnerTests
{
    [Fact]
    public void PickSkill_Matches_By_Id_In_Goal_Text()
    {
        var skill = new SkillManifest(
            "read-file", new SemVer(1, 0, 0), new string('a', 64),
            ExecutionEnvironment.Host, false, RiskTier.Read,
            TimeSpan.FromSeconds(30), TrustLevel.Low, "{}", "<x/>");

        var node = new TaskNode("n1", "please read-file at C:/tmp/x.txt", Array.Empty<string>());
        var picked = GoalRunner.PickSkill(node, new[] { skill });
        Assert.Equal("read-file", picked);
    }

    [Fact]
    public void PickSkill_Returns_Null_When_No_Match()
    {
        var skill = new SkillManifest(
            "execute-python", new SemVer(1, 0, 0), new string('a', 64),
            ExecutionEnvironment.Docker, false, RiskTier.Exec,
            TimeSpan.FromMinutes(1), TrustLevel.None, "{}", "<x/>");
        var node = new TaskNode("n1", "write markdown report", Array.Empty<string>());
        Assert.Null(GoalRunner.PickSkill(node, new[] { skill }));
    }

    [Fact]
    public void PickSkill_Empty_Catalog_Returns_Null()
    {
        var node = new TaskNode("n1", "anything", Array.Empty<string>());
        Assert.Null(GoalRunner.PickSkill(node, Array.Empty<SkillManifest>()));
    }
}
