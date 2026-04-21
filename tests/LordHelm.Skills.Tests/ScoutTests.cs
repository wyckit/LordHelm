using LordHelm.Scout;
using LordHelm.Scout.Parsers;

namespace LordHelm.Skills.Tests;

public class ScoutTests : IDisposable
{
    private readonly string _dbPath;

    public ScoutTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scout-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void GnuParser_Extracts_Short_And_Long_Flags()
    {
        const string help = """
            Usage: fake [options]

            Options:
              -h, --help               Show help
              -v, --version            Print version
                  --model <id>         Model identifier
                  --max-tokens <n>     Max output tokens
            """;
        var parser = new GnuStyleHelpParser("fake");
        var spec = parser.Parse(help, "fake 1.2.3", DateTimeOffset.UtcNow);
        Assert.Equal("1.2.3", spec.Version);
        Assert.Contains(spec.Flags, f => f.Name == "model" && f.Type == "id");
        Assert.Contains(spec.Flags, f => f.Name == "help" && f.ShortName == "h");
    }

    [Fact]
    public async Task Record_Detects_Added_And_Removed_Flags()
    {
        var store = new SqliteCliSpecStore(_dbPath);
        await store.InitializeAsync();

        var v1 = new CliSpec("fake", "1.0.0",
            new[] { new CliFlag("help", "h", null, null, "h"), new CliFlag("model", null, "id", null, "m") },
            DateTimeOffset.UtcNow);
        var v2 = new CliSpec("fake", "1.1.0",
            new[] { new CliFlag("help", "h", null, null, "h"), new CliFlag("temperature", null, "f", null, "t") },
            DateTimeOffset.UtcNow);

        var m1 = await store.RecordAsync(v1);
        Assert.Empty(m1);
        var m2 = await store.RecordAsync(v2);

        Assert.Contains(m2, m => m.Kind == MutationKind.Added && m.FlagName == "temperature");
        Assert.Contains(m2, m => m.Kind == MutationKind.Removed && m.FlagName == "model");
        Assert.Contains(m2, m => m.Kind == MutationKind.Archived);
    }

    [Fact]
    public async Task Stability_Threshold_Triggers_Promotion()
    {
        var store = new SqliteCliSpecStore(_dbPath);
        await store.InitializeAsync();

        var spec = new CliSpec("fake", "1.0.0",
            new[] { new CliFlag("help", null, null, null, "h") },
            DateTimeOffset.UtcNow);

        await store.RecordAsync(spec, stabilityThreshold: 3);
        await store.RecordAsync(spec, stabilityThreshold: 3);
        var third = await store.RecordAsync(spec, stabilityThreshold: 3);

        Assert.Contains(third, m => m.Kind == MutationKind.Promoted);
    }
}
