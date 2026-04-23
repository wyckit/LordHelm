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

    [Fact]
    public void PickSkill_Keyword_Routes_Create_Folder_To_Create_Directory()
    {
        var createDir = new SkillManifest("create-directory", new SemVer(1, 0, 0), new string('a', 64),
            ExecutionEnvironment.Host, true, RiskTier.Write,
            TimeSpan.FromSeconds(10), TrustLevel.Medium, "{}", "<x/>");
        var node = new TaskNode("n1", "Create a new folder named TestCli at C:\\Software\\TestCli", Array.Empty<string>());
        Assert.Equal("create-directory", GoalRunner.PickSkill(node, new[] { createDir }));
    }

    [Theory]
    [InlineData("Create a new folder named TestCli at C:\\Software\\TestCli", "C:\\Software\\TestCli")]
    [InlineData("Make directory at /usr/local/bin/tools",                     "/usr/local/bin/tools")]
    [InlineData("create folder \"/tmp/my folder with spaces\"",               "/tmp/my folder with spaces")]
    public void ExtractArgsForSkill_Lifts_Path_From_Goal(string goal, string expected)
    {
        var json = GoalRunner.ExtractArgsForSkill("create-directory", goal);
        Assert.NotNull(json);
        var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.Equal(expected, doc.RootElement.GetProperty("path").GetString());
    }
}
