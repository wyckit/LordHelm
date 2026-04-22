using LordHelm.Core;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Overseers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class OverseerTests
{
    // ---------------------------------------------------- alert tray

    [Fact]
    public async Task Tray_UnreadCount_And_Mark_Read_Work()
    {
        var tray = new InMemoryAlertTray();
        await tray.PushAsync("test", AlertKind.Info, "t1", "b");
        await tray.PushAsync("test", AlertKind.Warning, "t2", "b");
        Assert.Equal(2, tray.UnreadCount);
        var first = tray.All()[1];
        await tray.MarkReadAsync(first.Id);
        Assert.Equal(1, tray.UnreadCount);
        await tray.MarkAllReadAsync();
        Assert.Equal(0, tray.UnreadCount);
    }

    [Fact]
    public async Task Tray_OnChanged_Fires_On_Push()
    {
        var tray = new InMemoryAlertTray();
        int hits = 0;
        tray.OnChanged += () => hits++;
        await tray.PushAsync("x", AlertKind.Info, "a", "b");
        Assert.Equal(1, hits);
    }

    // ---------------------------------------------------- registry

    [Fact]
    public void Registry_Records_Tick_And_Advances_NextTick()
    {
        var reg = new OverseerRegistry();
        var agent = new StubAgent(id: "a", interval: TimeSpan.FromMinutes(5));
        reg.Register(agent);
        var now = DateTimeOffset.UtcNow;
        reg.RecordTick(agent.Id, new OverseerResult(OverseerStatus.Working, "ok"), now, agent.DefaultInterval);
        var state = reg.Get(agent.Id)!;
        Assert.Equal(1, state.TickCount);
        Assert.Equal(OverseerStatus.Working, state.LastStatus);
        Assert.Equal(now + agent.DefaultInterval, state.NextTickAt);
    }

    [Fact]
    public void Registry_Honours_NextIntervalOverride()
    {
        var reg = new OverseerRegistry();
        var agent = new StubAgent(id: "a", interval: TimeSpan.FromMinutes(5));
        reg.Register(agent);
        var now = DateTimeOffset.UtcNow;
        reg.RecordTick(agent.Id,
            new OverseerResult(OverseerStatus.Working, null, NextIntervalOverride: TimeSpan.FromSeconds(3)),
            now, agent.DefaultInterval);
        var state = reg.Get(agent.Id)!;
        Assert.Equal(now + TimeSpan.FromSeconds(3), state.NextTickAt);
    }

    [Fact]
    public void Registry_DoneForNow_Pauses_Agent()
    {
        var reg = new OverseerRegistry();
        var agent = new StubAgent(id: "a", interval: TimeSpan.FromMinutes(5));
        reg.Register(agent);
        var now = DateTimeOffset.UtcNow;
        reg.RecordTick(agent.Id, new OverseerResult(OverseerStatus.DoneForNow, "clean"), now, agent.DefaultInterval);
        var state = reg.Get(agent.Id)!;
        Assert.True(state.NextTickAt > now.AddDays(300));
    }

    [Fact]
    public void Registry_Bump_Resets_NextTick_To_Now()
    {
        var reg = new OverseerRegistry();
        var agent = new StubAgent(id: "a", interval: TimeSpan.FromMinutes(5));
        reg.Register(agent);
        reg.RecordTick(agent.Id, new OverseerResult(OverseerStatus.Working), DateTimeOffset.UtcNow, agent.DefaultInterval);
        reg.Bump(agent.Id);
        var state = reg.Get(agent.Id)!;
        Assert.True(state.NextTickAt <= DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------- runner

    [Fact]
    public async Task Runner_Ticks_Enabled_Agent_And_Skips_Disabled()
    {
        var reg = new OverseerRegistry();
        var tray = new InMemoryAlertTray();
        var services = new ServiceCollection().BuildServiceProvider();
        var runner = new OverseerRunner(reg, tray, services, new OverseerRunnerOptions(),
            NullLogger<OverseerRunner>.Instance);

        var onAgent = new CountingAgent("on", TimeSpan.FromMilliseconds(1));
        var offAgent = new CountingAgent("off", TimeSpan.FromMilliseconds(1));
        reg.Register(onAgent, enabledByDefault: true);
        reg.Register(offAgent, enabledByDefault: false);

        await runner.SweepOnceAsync(CancellationToken.None);
        // Give the launched task a moment to run
        await Task.Delay(80);

        Assert.True(onAgent.TickCount >= 1, $"expected on-agent to tick, got {onAgent.TickCount}");
        Assert.Equal(0, offAgent.TickCount);
    }

    [Fact]
    public async Task Runner_Pushes_Error_Alert_When_Agent_Throws()
    {
        var reg = new OverseerRegistry();
        var tray = new InMemoryAlertTray();
        var services = new ServiceCollection().BuildServiceProvider();
        var runner = new OverseerRunner(reg, tray, services, new OverseerRunnerOptions(),
            NullLogger<OverseerRunner>.Instance);

        reg.Register(new ThrowingAgent("bad"));

        await runner.SweepOnceAsync(CancellationToken.None);
        await Task.Delay(120);

        Assert.Contains(tray.All(), a => a.Kind == AlertKind.Error && a.Source == "bad");
        Assert.Equal(OverseerStatus.Error, reg.Get("bad")!.LastStatus);
    }

    // ---------------------------------------------------- DocumentCurator worked example

    [Fact]
    public async Task DocumentCurator_Reports_Missing_Overview_And_Missing_Description()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-curator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "skills"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "LordHelm.Sample"));
        await File.WriteAllTextAsync(Path.Combine(dir, "skills", "noisy.skill.xml"),
            // missing <Description> and <Tags>
            "<Skill><Id>noisy</Id></Skill>");
        await File.WriteAllTextAsync(Path.Combine(dir, "src", "LordHelm.Sample", "Foo.cs"),
            "public class Foo {}");

        var tray = new InMemoryAlertTray();
        var agent = new DocumentCuratorAgent(dir, NullLogger<DocumentCuratorAgent>.Instance);
        var ctx = new OverseerContext(tray, new ServiceCollection().BuildServiceProvider(), 1, DateTimeOffset.UtcNow);
        var result = await agent.TickAsync(ctx, CancellationToken.None);

        Assert.Equal(OverseerStatus.Working, result.Status);
        Assert.Contains(tray.All(), a => a.Kind == AlertKind.Attention);

        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ---------------------------------------------------- helpers

    private sealed class StubAgent : IOverseerAgent
    {
        public StubAgent(string id, TimeSpan interval) { Id = id; DefaultInterval = interval; }
        public string Id { get; }
        public string Name => Id;
        public string Description => "test";
        public TimeSpan DefaultInterval { get; }
        public Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct) =>
            Task.FromResult(new OverseerResult(OverseerStatus.Working));
    }

    private sealed class CountingAgent : IOverseerAgent
    {
        public CountingAgent(string id, TimeSpan interval) { Id = id; DefaultInterval = interval; }
        public string Id { get; }
        public string Name => Id;
        public string Description => "test";
        public TimeSpan DefaultInterval { get; }
        public int TickCount;
        public Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref TickCount);
            return Task.FromResult(new OverseerResult(OverseerStatus.Working));
        }
    }

    private sealed class ThrowingAgent : IOverseerAgent
    {
        public ThrowingAgent(string id) { Id = id; }
        public string Id { get; }
        public string Name => Id;
        public string Description => "test";
        public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
        public Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
