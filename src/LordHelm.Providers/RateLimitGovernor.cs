namespace LordHelm.Providers;

/// <summary>
/// Sliding-window token-bucket governor: admits up to `MaxCallsPerWindow` in any
/// `Window`. Oldest timestamps age out naturally. Thread-safe.
/// </summary>
public sealed class RateLimitGovernor
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _calls = new();
    public int MaxCallsPerWindow { get; }
    public TimeSpan Window { get; }

    public RateLimitGovernor(int maxCallsPerWindow, TimeSpan window)
    {
        MaxCallsPerWindow = maxCallsPerWindow;
        Window = window;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan? wait = null;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                while (_calls.Count > 0 && now - _calls.Peek() > Window)
                    _calls.Dequeue();
                if (_calls.Count < MaxCallsPerWindow)
                {
                    _calls.Enqueue(now);
                    return;
                }
                wait = Window - (now - _calls.Peek()) + TimeSpan.FromMilliseconds(10);
            }
            await Task.Delay(wait.Value, ct);
        }
    }

    public int InFlight
    {
        get { lock (_gate) return _calls.Count; }
    }
}
