using LordHelm.Core;
using LordHelm.Orchestrator.Usage;

namespace LordHelm.Consensus.Tests;

public class UsageAccumulatorTests
{
    [Fact]
    public void Record_Accumulates_Requests_Tokens_And_Cost_Per_Vendor()
    {
        var state = new UsageState();
        var acc = new UsageAccumulator(state);

        acc.Record("claude", "claude-haiku-4-5", new UsageRecord(100, 50, 0), 0.01m);
        acc.Record("claude", "claude-haiku-4-5", new UsageRecord(200, 100, 0), 0.02m);
        acc.Record("gemini", "gemini-2.5-flash-lite", new UsageRecord(50, 25, 0), 0.005m);

        var claude = state.Get("claude");
        Assert.NotNull(claude);
        Assert.Equal(2, claude!.RequestsUsed);
        Assert.Equal(450L, claude.TokensUsed); // 100+50+200+100
        Assert.Equal(0.03m, claude.CostUsd);
        Assert.Equal("claude-haiku-4-5", claude.ResolvedModel);
        Assert.True(claude.AuthOk);

        var gemini = state.Get("gemini");
        Assert.NotNull(gemini);
        Assert.Equal(1, gemini!.RequestsUsed);
        Assert.Equal(75L, gemini.TokensUsed);
    }

    [Fact]
    public void Record_Preserves_Probe_Derived_Exhausted_Flag_Across_Updates()
    {
        // Auth probe flips Exhausted=true; subsequent real-call Records
        // should keep that signal until the exhaustion monitor clears it.
        var state = new UsageState();
        state.Update(new UsageSnapshot(
            "claude", null, null, null, null, null, null,
            AuthOk: false, Exhausted: true, ResolvedModel: null,
            RawOutput: null, Error: "quota", ProbedAt: DateTimeOffset.UtcNow));

        var acc = new UsageAccumulator(state);
        acc.Record("claude", "claude-opus-4-7", new UsageRecord(10, 5, 0), 0.001m);

        var snap = state.Get("claude");
        Assert.NotNull(snap);
        Assert.True(snap!.Exhausted);          // probe signal preserved
        Assert.Equal("quota", snap.Error);     // error preserved too
        Assert.Equal(1, snap.RequestsUsed);    // usage still merged
    }

    [Fact]
    public void OnChanged_Fires_On_Update_And_Clear()
    {
        var state = new UsageState();
        var hits = 0;
        state.OnChanged += () => hits++;

        state.Update(new UsageSnapshot("v", 0, null, 0, null, 0m, null,
            AuthOk: true, Exhausted: false, ResolvedModel: "m",
            RawOutput: null, Error: null, ProbedAt: DateTimeOffset.UtcNow));
        Assert.Equal(1, hits);

        state.Clear();
        Assert.Equal(2, hits);
    }
}
