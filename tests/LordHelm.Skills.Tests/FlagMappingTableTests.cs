using LordHelm.Skills.Transpilation;

namespace LordHelm.Skills.Tests;

public class FlagMappingTableTests
{
    [Fact]
    public void Default_Includes_Curated_Mappings()
    {
        var t = FlagMappingTable.Default();
        Assert.Equal("--output-format", t.Lookup("claude", "2.1.0", "outputFormat"));
        Assert.Equal("--format", t.Lookup("gemini", "1.0.0", "outputFormat"));
    }

    [Fact]
    public void Hydrate_Adds_Kebab_Flags_As_CamelCase_Canonical()
    {
        var t = FlagMappingTable.Default();
        t.Hydrate("claude", "2.3.0", new[]
        {
            ("session-id", (string?)null),
            ("disallowed-tools", (string?)null),
        });

        Assert.Equal("--session-id", t.Lookup("claude", "2.3.0", "sessionId"));
        Assert.Equal("--disallowed-tools", t.Lookup("claude", "2.3.0", "disallowedTools"));
        // Curated still wins for known canonicals
        Assert.Equal("--output-format", t.Lookup("claude", "2.3.0", "outputFormat"));
    }

    [Fact]
    public void DropVendorVersioned_Leaves_Wildcard_Curated_Intact()
    {
        var t = FlagMappingTable.Default();
        t.Hydrate("claude", "2.3.0", new[] { ("session-id", (string?)null) });
        Assert.Equal("--session-id", t.Lookup("claude", "2.3.0", "sessionId"));

        t.DropVendorVersioned("claude");
        Assert.Null(t.Lookup("claude", "2.3.0", "sessionId"));
        // wildcard curated entry for outputFormat should still resolve
        Assert.Equal("--output-format", t.Lookup("claude", "2.3.0", "outputFormat"));
    }

    [Fact]
    public async Task Concurrent_Hydrate_Is_Snapshot_Consistent()
    {
        var t = FlagMappingTable.Default();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                t.Hydrate("claude", "v" + i, new[] { ($"flag-{i}", (string?)null) });
                i++;
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = t.Lookup("claude", "*", "outputFormat");
            }
        });

        await writer;
        await reader;
        Assert.Equal("--output-format", t.Lookup("claude", "*", "outputFormat"));
    }
}
