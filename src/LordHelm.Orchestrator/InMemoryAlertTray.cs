using System.Collections.Concurrent;
using LordHelm.Core;

namespace LordHelm.Orchestrator;

/// <summary>
/// Default in-process <see cref="IAlertTray"/>. Keeps the last N alerts in memory;
/// older ones fall off. Thread-safe. <see cref="OnChanged"/> fires on every push
/// and read-state change so the Blazor layer can re-render without polling.
/// </summary>
public sealed class InMemoryAlertTray : IAlertTray
{
    private readonly ConcurrentQueue<AlertEntry> _entries = new();
    private readonly ConcurrentDictionary<string, byte> _readIds = new();
    private readonly int _cap;

    public InMemoryAlertTray(int capacity = 256)
    {
        _cap = capacity;
    }

    public event Action? OnChanged;

    public Task<AlertEntry> PushAsync(string source, AlertKind kind, string title, string body, CancellationToken ct = default)
    {
        var entry = new AlertEntry(
            Id: Guid.NewGuid().ToString("N"),
            Source: source,
            Kind: kind,
            Title: title,
            Body: body,
            At: DateTimeOffset.UtcNow,
            Read: false);
        _entries.Enqueue(entry);
        while (_entries.Count > _cap && _entries.TryDequeue(out var dropped))
        {
            _readIds.TryRemove(dropped.Id, out _);
        }
        OnChanged?.Invoke();
        return Task.FromResult(entry);
    }

    public IReadOnlyList<AlertEntry> All() =>
        _entries.Reverse()
            .Select(e => e with { Read = _readIds.ContainsKey(e.Id) })
            .ToList();

    public int UnreadCount =>
        _entries.Count(e => !_readIds.ContainsKey(e.Id));

    public Task MarkReadAsync(string id, CancellationToken ct = default)
    {
        _readIds.TryAdd(id, 0);
        OnChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task MarkAllReadAsync(CancellationToken ct = default)
    {
        foreach (var e in _entries) _readIds.TryAdd(e.Id, 0);
        OnChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        while (_entries.TryDequeue(out _)) { }
        _readIds.Clear();
        OnChanged?.Invoke();
        return Task.CompletedTask;
    }
}
