using System.Threading.Channels;
using LordHelm.Monitor;
using LordHelm.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Execution.Tests;

public class SseBroadcasterTests
{
    private sealed class FakeMonitor : IProcessMonitor
    {
        private readonly Channel<ProcessEvent> _ch = Channel.CreateUnbounded<ProcessEvent>();
        public ChannelReader<ProcessEvent> Events => _ch.Reader;
        public IReadOnlyDictionary<string, LogRing> Logs { get; } = new Dictionary<string, LogRing>();
        public ProcessHandle Launch(LaunchSpec spec, CancellationToken ct = default) =>
            new(spec.SubprocessId, Task.FromResult(0), new LogRing());
        public ValueTask PublishAsync(ProcessEvent ev) => _ch.Writer.WriteAsync(ev);
    }

    [Fact]
    public async Task Subscriber_Receives_Events_For_Its_Subprocess_Only()
    {
        var mon = new FakeMonitor();
        var bcast = new SseLogBroadcaster(mon);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var loop = Task.Run(() => StartAsync(bcast, cts.Token));

        await using var sub = bcast.Subscribe("target");
        await mon.PublishAsync(new ProcessEvent("other", "x", ProcessEventKind.Stdout, "noise", null, null, null, DateTimeOffset.UtcNow));
        await mon.PublishAsync(new ProcessEvent("target", "x", ProcessEventKind.Stdout, "hello", null, null, null, DateTimeOffset.UtcNow));

        var ev = await sub.Reader.ReadAsync(cts.Token);
        Assert.Equal("target", ev.SubprocessId);
        Assert.Equal("hello", ev.Line);

        cts.Cancel();
        try { await loop; } catch { }
    }

    [Fact]
    public async Task Unsubscribe_Completes_Reader()
    {
        var mon = new FakeMonitor();
        var bcast = new SseLogBroadcaster(mon);
        var sub = bcast.Subscribe("a");
        await sub.DisposeAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var read = sub.Reader.ReadAllAsync(cts.Token).GetAsyncEnumerator();
        try
        {
            Assert.False(await read.MoveNextAsync());
        }
        finally { await read.DisposeAsync(); }
    }

    private static Task StartAsync(SseLogBroadcaster svc, CancellationToken ct)
    {
        // Use reflection-free shim: BackgroundService exposes StartAsync via IHostedService
        var hosted = (Microsoft.Extensions.Hosting.IHostedService)svc;
        return hosted.StartAsync(ct);
    }
}
