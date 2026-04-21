using LordHelm.Core;
using LordHelm.Skills;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Skills.Tests;

public class LoaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lordhelm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "skills.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SkillLoader NewLoader() =>
        new(new SqliteSkillCache(_dbPath), new ManifestValidator(),
            NullLogger<SkillLoader>.Instance);

    [Fact]
    public async Task Loads_Seed_Skills_From_Disk()
    {
        var seedSource = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "skills");
        var seedDir = Path.GetFullPath(seedSource);
        var workingDir = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(workingDir);
        foreach (var file in Directory.GetFiles(seedDir, "*.skill.xml"))
        {
            File.Copy(file, Path.Combine(workingDir, Path.GetFileName(file)));
        }

        var loader = NewLoader();
        var result = await loader.LoadDirectoryAsync(workingDir);

        Assert.Equal(3, result.TotalFiles);
        Assert.Equal(3, result.Loaded);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public async Task Rerun_Skips_Unchanged_Files()
    {
        var skillPath = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(skillPath);
        await File.WriteAllTextAsync(
            Path.Combine(skillPath, "t.skill.xml"),
            Minimal);

        var loader = NewLoader();
        var first = await loader.LoadDirectoryAsync(skillPath);
        var second = await loader.LoadDirectoryAsync(skillPath);

        Assert.Equal(1, first.Loaded);
        Assert.Equal(0, second.Loaded);
        Assert.Equal(1, second.SkippedUnchanged);
    }

    [Fact]
    public async Task Invalid_File_Is_Reported_And_Others_Load()
    {
        var dir = Path.Combine(_dir, "mixed");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "ok.skill.xml"), Minimal);
        await File.WriteAllTextAsync(Path.Combine(dir, "bad.skill.xml"), "<not-a-skill/>");

        var loader = NewLoader();
        var r = await loader.LoadDirectoryAsync(dir);

        Assert.Equal(1, r.Loaded);
        Assert.Single(r.Invalid);
    }

    [Fact]
    public async Task Parse_Populates_Ast_Fields()
    {
        var manifest = SkillManifestParser.Parse(Minimal);
        Assert.Equal("t1", manifest.Id);
        Assert.Equal(ExecutionEnvironment.Host, manifest.ExecEnv);
        Assert.Equal(RiskTier.Read, manifest.RiskTier);
        Assert.Equal(TrustLevel.Low, manifest.MinTrust);
        Assert.Equal(TimeSpan.FromSeconds(5), manifest.Timeout);
        Assert.False(manifest.RequiresApproval);
        Assert.Equal(64, manifest.ContentHashSha256.Length);
    }

    private const string Minimal = """
        <?xml version="1.0" encoding="utf-8"?>
        <Skill xmlns="https://lordhelm.dev/schemas/skill-manifest/v1">
          <Id>t1</Id>
          <Version>0.1.0</Version>
          <ExecutionEnvironment>Host</ExecutionEnvironment>
          <RequiresApproval>false</RequiresApproval>
          <RiskTier>Read</RiskTier>
          <Timeout>PT5S</Timeout>
          <MinTrust>Low</MinTrust>
          <ParameterSchema><![CDATA[{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}]]></ParameterSchema>
        </Skill>
        """;
}
