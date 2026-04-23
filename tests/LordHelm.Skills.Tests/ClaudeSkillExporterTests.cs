using LordHelm.Core;
using LordHelm.Skills.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Skills.Tests;

public class ClaudeSkillExporterTests : IDisposable
{
    private readonly string _tempHome;

    public ClaudeSkillExporterTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "lh-claude-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }

    private const string CanonicalXml =
        "<Skill xmlns=\"https://lordhelm.dev/schemas/skill-manifest/v1\">" +
            "<Id>sql-optimizer</Id>" +
            "<Version>0.1.0</Version>" +
            "<ExecutionEnvironment>Host</ExecutionEnvironment>" +
            "<RequiresApproval>false</RequiresApproval>" +
            "<RiskTier>Read</RiskTier>" +
            "<Timeout>PT2M</Timeout>" +
            "<MinTrust>Low</MinTrust>" +
            "<Description>Analyze slow SQL queries.</Description>" +
            "<Tags><Tag>sql</Tag><Tag>performance</Tag></Tags>" +
            "<ParameterSchema><![CDATA[{\"type\":\"object\"}]]></ParameterSchema>" +
        "</Skill>";

    private static SkillManifest Sample() => new(
        Id: "sql-optimizer",
        Version: new SemVer(0, 1, 0),
        ContentHashSha256: new string('a', 64),
        ExecEnv: ExecutionEnvironment.Host,
        RequiresApproval: false,
        RiskTier: RiskTier.Read,
        Timeout: TimeSpan.FromMinutes(2),
        MinTrust: TrustLevel.Low,
        ParameterSchemaJson: "{\"type\":\"object\"}",
        CanonicalXml: CanonicalXml);

    [Fact]
    public void BuildSkillMd_Has_Yaml_Frontmatter_With_Name_And_Description()
    {
        var md = ClaudeSkillExporter.BuildSkillMd(Sample());
        Assert.StartsWith("---\nname: sql-optimizer\ndescription: Analyze slow SQL queries.\n---",
            md.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExtractFromXml_Pulls_Description_And_Tags()
    {
        var (desc, tags) = ClaudeSkillExporter.ExtractFromXml(CanonicalXml);
        Assert.Equal("Analyze slow SQL queries.", desc);
        Assert.Equal(new[] { "sql", "performance" }, tags);
    }

    [Fact]
    public async Task ExportAsync_Writes_SKILL_md_To_Skill_Subdir()
    {
        var exporter = new ClaudeSkillExporter(NullLogger<ClaudeSkillExporter>.Instance, overrideDir: _tempHome);
        var report = await exporter.ExportAsync(new[] { Sample() }, overwrite: false);

        Assert.Equal(1, report.Written);
        Assert.Equal(0, report.Skipped);
        Assert.Empty(report.Errors);

        var expected = Path.Combine(_tempHome, ".claude", "skills", "sql-optimizer", "SKILL.md");
        Assert.True(File.Exists(expected));
        var body = await File.ReadAllTextAsync(expected);
        Assert.Contains("name: sql-optimizer", body);
        Assert.Contains("Analyze slow SQL queries", body);
        Assert.Contains("**Tags:** sql, performance", body);
    }

    [Fact]
    public async Task ExportAsync_Skips_Existing_When_Overwrite_False()
    {
        var exporter = new ClaudeSkillExporter(NullLogger<ClaudeSkillExporter>.Instance, overrideDir: _tempHome);
        var first  = await exporter.ExportAsync(new[] { Sample() }, overwrite: false);
        var second = await exporter.ExportAsync(new[] { Sample() }, overwrite: false);
        Assert.Equal(1, first.Written);
        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task ExportAsync_Overwrites_When_Flag_Is_True()
    {
        var exporter = new ClaudeSkillExporter(NullLogger<ClaudeSkillExporter>.Instance, overrideDir: _tempHome);
        var first  = await exporter.ExportAsync(new[] { Sample() }, overwrite: false);
        var second = await exporter.ExportAsync(new[] { Sample() }, overwrite: true);
        Assert.Equal(1, first.Written);
        Assert.Equal(1, second.Written);
        Assert.Equal(0, second.Skipped);
    }

    [Fact]
    public void IsSupported_False_When_Home_Missing()
    {
        var exporter = new ClaudeSkillExporter(NullLogger<ClaudeSkillExporter>.Instance, overrideDir: null);
        // Depends on actual env having HOME/USERPROFILE (usually true).
        Assert.Equal(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USERPROFILE"))
                 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")),
            exporter.IsSupported());
    }

    [Fact]
    public async Task UnsupportedExporter_Reports_Reason()
    {
        var exporter = new UnsupportedSkillExporter("codex", "codex has no skills dir yet");
        var report = await exporter.ExportAsync(new[] { Sample() }, overwrite: false);
        Assert.False(exporter.IsSupported());
        Assert.Equal(0, report.Written);
        Assert.Single(report.Errors);
        Assert.Contains("codex has no skills dir", report.Errors[0]);
    }
}
