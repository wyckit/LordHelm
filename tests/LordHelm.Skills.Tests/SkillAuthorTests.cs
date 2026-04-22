using LordHelm.Skills;

namespace LordHelm.Skills.Tests;

public class SkillAuthorTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _skillsDir;

    public SkillAuthorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "helm-author-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_root, "skills.db");
        _skillsDir = Path.Combine(_root, "skills");
        Directory.CreateDirectory(_skillsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SkillAuthor New() =>
        new(new ManifestValidator(), new SqliteSkillCache(_dbPath), _skillsDir);

    private const string Minimal = """
        <?xml version="1.0" encoding="utf-8"?>
        <Skill xmlns="https://lordhelm.dev/schemas/skill-manifest/v1">
          <Id>author-test</Id>
          <Version>0.1.0</Version>
          <ExecutionEnvironment>Host</ExecutionEnvironment>
          <RequiresApproval>false</RequiresApproval>
          <RiskTier>Read</RiskTier>
          <Timeout>PT10S</Timeout>
          <MinTrust>Low</MinTrust>
          <ParameterSchema><![CDATA[{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}]]></ParameterSchema>
        </Skill>
        """;

    [Fact]
    public async Task Saves_A_Valid_Manifest_To_Skills_Dir()
    {
        var author = New();
        var result = await author.SaveAsync(Minimal);

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.NotNull(result.Manifest);
        Assert.Equal("author-test", result.Manifest!.Id);
        Assert.True(File.Exists(result.SavedPath));
        Assert.EndsWith("author-test.skill.xml", result.SavedPath!);
    }

    [Fact]
    public async Task Rejects_Invalid_Manifest_Without_Writing_File()
    {
        var author = New();
        var bad = Minimal.Replace("<RiskTier>Read</RiskTier>", "<RiskTier>Nope</RiskTier>");
        var result = await author.SaveAsync(bad);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Errors, e => e.Stage == ValidationStage.Xsd);
        Assert.Empty(Directory.GetFiles(_skillsDir));
    }

    [Fact]
    public async Task Refuses_Overwrite_By_Default()
    {
        var author = New();
        (await author.SaveAsync(Minimal)).GetType(); // first save succeeds

        var result = await author.SaveAsync(Minimal);
        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.ErrorDetail);
    }

    [Fact]
    public async Task Overwrites_When_Explicitly_Requested()
    {
        var author = New();
        var first = await author.SaveAsync(Minimal);
        Assert.True(first.Succeeded);

        var updated = Minimal.Replace("<Version>0.1.0</Version>", "<Version>0.2.0</Version>");
        var result = await author.SaveAsync(updated, overwrite: true);

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.Equal("0.2.0", result.Manifest!.Version.ToString());
    }
}
