using LordHelm.Core;
using LordHelm.Providers.Adapters;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Consensus.Tests;

/// <summary>
/// Contract tests for the AdapterRouter's scoring when all three real
/// adapters are registered with their production capabilities. If codex stops
/// winning for "code"-adjacent kinds or generic-chat kinds tilt too hard
/// toward claude, these tests catch it.
/// </summary>
public class AdapterRoutingScoreboardTests
{
    private static AdapterRouter NewRouter()
    {
        // Real adapters with their production capabilities. The underlying
        // CLI clients are never invoked here — only Rank() scoring is tested.
        var claude = new ClaudeCodeAdapter(new ClaudeCliModelClient());
        var gemini = new GeminiCliAdapter(new GeminiCliModelClient());
        var codex  = new CodexCliAdapter(new CodexCliModelClient());
        var registry = new AgentAdapterRegistry(new IAgentModelAdapter[] { claude, gemini, codex });
        return new AdapterRouter(registry);
    }

    private static string Winner(AdapterRouter r, string kind)
    {
        var ranked = r.Rank(new RoutingRequest(
            TaskKind: kind, EstimatedContextTokens: 2000,
            NeedsToolCalls: false, PreferredMode: ResourceMode.Interactive));
        return ranked.FirstOrDefault()?.VendorId ?? "(none)";
    }

    [Theory]
    // codex-declared kinds — codex MUST be top
    [InlineData("refactor",        "codex")]
    [InlineData("test-gen",        "codex")]
    [InlineData("sandbox-exec",    "codex")]
    // claude-declared kinds — claude top
    [InlineData("reasoning",       "claude")]
    [InlineData("review",          "claude")]
    [InlineData("architecture",    "claude")]
    [InlineData("docs",            "claude")]
    // gemini-declared kinds — gemini top
    [InlineData("research",        "gemini")]
    [InlineData("multimodal",      "gemini")]
    [InlineData("long-context",    "gemini")]
    [InlineData("summarisation",   "gemini")]
    [InlineData("security-review", "gemini")]
    public void Router_Picks_Expected_Winner_For_Declared_Kinds(string kind, string expected)
    {
        var router = NewRouter();
        Assert.Equal(expected, Winner(router, kind));
    }

    [Fact]
    public void Shared_Code_Kind_Ties_On_Capability_But_Claude_Wins_On_Mode_And_Cost()
    {
        // "code" is declared by BOTH claude and codex. Claude is Interactive
        // mode (latency_fit=1.0) while codex is Builder (0.7); claude is also
        // cheaper in the seeded cost profiles. So for a generic "code"
        // request with PreferredMode=Interactive, claude should win — that's
        // WHY codex's request count stays low in an interactive-heavy workload.
        // This test encodes the observed behavior; flipping expected to codex
        // means we've re-weighted modes (legitimate) or broken capability
        // matching (regression).
        var router = NewRouter();
        Assert.Equal("claude", Winner(router, "code"));
    }

    [Theory]
    // Generic kinds not declared by anyone — capability match is 0 across the
    // board, so the tiebreak cascade (mode / cost / latency) decides. All of
    // these currently go to claude because it's Interactive + cheapest.
    [InlineData("chat")]
    [InlineData("dispatch")]
    [InlineData("decompose")]
    [InlineData("synthesis")]
    public void Undeclared_Kinds_Fall_To_Claude_By_Default(string kind)
    {
        var router = NewRouter();
        Assert.Equal("claude", Winner(router, kind));
    }

    [Fact]
    public void Codex_Wins_For_Code_When_Mode_Is_Builder()
    {
        // Flipping PreferredMode=Builder (long-running builds, background
        // agents) should push codex ahead of claude on the "code" kind. If
        // this stops being true, the mode weighting has broken.
        var router = NewRouter();
        var ranked = router.Rank(new RoutingRequest(
            TaskKind: "code", EstimatedContextTokens: 2000,
            NeedsToolCalls: false, PreferredMode: ResourceMode.Builder));
        Assert.Equal("codex", ranked.First().VendorId);
    }
}
