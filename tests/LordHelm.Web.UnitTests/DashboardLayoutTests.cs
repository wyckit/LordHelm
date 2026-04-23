using LordHelm.Web.Layout;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Web.UnitTests;

public class DashboardLayoutTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public DashboardLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helm-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "dashboard-layout.json");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public void SwapByIndex_Reorders_And_Fires_OnChanged()
    {
        var state = new DashboardLayoutState();
        var hits = 0;
        state.OnChanged += () => hits++;
        var before = state.Entries;
        state.SwapByIndex(0, 1);
        Assert.Equal(1, hits);
        var after = state.Entries;
        Assert.Equal(before[0].Id, after[1].Id);
        Assert.Equal(before[1].Id, after[0].Id);
    }

    [Fact]
    public void Replace_With_PresetKey_Stores_Key()
    {
        var state = new DashboardLayoutState();
        state.Replace(new[] { new LayoutEntry("topology", 12, 3) }, presetKey: "incident");
        Assert.Equal("incident", state.CurrentPresetKey);
        Assert.Single(state.Entries);
    }

    [Fact]
    public void Swap_After_Preset_Clears_PresetKey()
    {
        var state = new DashboardLayoutState();
        state.Replace(new[] { new LayoutEntry("a", 3, 2), new LayoutEntry("b", 3, 2) }, presetKey: "cost");
        state.SwapByIndex(0, 1);
        Assert.Null(state.CurrentPresetKey);
    }

    [Fact]
    public async Task Roundtrip_Via_JsonFile_Store()
    {
        var state = new DashboardLayoutState();
        state.Replace(new[]
        {
            new LayoutEntry("topology", 12, 4),
            new LayoutEntry("alerts",   3, 2),
        }, presetKey: "incident");

        var store = new JsonFileDashboardLayoutStore(_path, NullLogger<JsonFileDashboardLayoutStore>.Instance);
        await store.SaveAsync(state);

        var state2 = new DashboardLayoutState();
        await store.LoadAsync(state2);
        Assert.Equal("incident", state2.CurrentPresetKey);
        Assert.Equal(2, state2.Entries.Count);
        Assert.Equal("topology", state2.Entries[0].Id);
    }

    [Theory]
    [InlineData("Generate dashboard for incident view", "incident")]
    [InlineData("Focus on code agents",                "focus")]
    [InlineData("What changed overnight?",             "overnight")]
    [InlineData("Show me spend this week",             "cost")]
    [InlineData("What's waiting on approval?",         "approval")]
    [InlineData("random nonsense blorp",               null)]
    public void Regex_Preset_Matcher_Recognises_Design_Handoff_Phrases(string prompt, string? expected)
    {
        var resolver = new LayoutPresetResolver(experts: null!);
        var preset = resolver.MatchByRegex(prompt);
        Assert.Equal(expected, preset?.Key);
    }
}
