using System.Collections.Concurrent;
using System.Threading.Channels;
using LordHelm.Monitor;
using Microsoft.Extensions.Hosting;

namespace LordHelm.Web;

/// <summary>
/// Fan-out from the single <see cref="IProcessMonitor.Events"/> channel to per-subprocess
/// bounded channels that SSE subscribers drain. A subscriber calls <see cref="Subscribe"/>
/// with a subprocess id, receives its own Channel to read from, and disposes when the HTTP
/// connection closes. Late subscribers only see events that arrive after they subscribe —
/// they do not replay history (the ring buffer inside the Watcher covers that).
/// </summary>
public sealed class SseLogBroadcaster : BackgroundService
{
    private readonly IProcessMonitor _monitor;
    private readonly ConcurrentDictionary<string, List<Channel<ProcessEvent>>> _subscribers = new();

    public SseLogBroadcaster(IProcessMonitor monitor) { _monitor = monitor; }

    public Subscription Subscribe(string subprocessId)
    {
        var channel = Channel.CreateBounded<ProcessEvent>(new BoundedChannelOptions(capacity: 512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var list = _subscribers.GetOrAdd(subprocessId, _ => new List<Channel<ProcessEvent>>());
        lock (list) list.Add(channel);
        return new Subscription(this, subprocessId, channel);
    }

    internal void Unsubscribe(string subprocessId, Channel<ProcessEvent> channel)
    {
        if (_subscribers.TryGetValue(subprocessId, out var list))
        {
            lock (list) list.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var ev in _monitor.Events.ReadAllAsync(ct))
        {
            if (!_subscribers.TryGetValue(ev.SubprocessId, out var list)) continue;
            Channel<ProcessEvent>[] snapshot;
            lock (list) snapshot = list.ToArray();
            foreach (var ch in snapshot)
            {
                // BoundedChannelFullMode.DropOldest makes this non-blocking.
                ch.Writer.TryWrite(ev);
            }
        }
    }

    public sealed class Subscription : IAsyncDisposable
    {
        private readonly SseLogBroadcaster _owner;
        private readonly string _subprocessId;
        private readonly Channel<ProcessEvent> _channel;

        public ChannelReader<ProcessEvent> Reader => _channel.Reader;

        internal Subscription(SseLogBroadcaster owner, string subprocessId, Channel<ProcessEvent> channel)
        {
            _owner = owner;
            _subprocessId = subprocessId;
            _channel = channel;
        }

        public ValueTask DisposeAsync()
        {
            _owner.Unsubscribe(_subprocessId, _channel);
            return ValueTask.CompletedTask;
        }
    }
}
