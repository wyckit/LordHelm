using System.Collections.Concurrent;
using LordHelm.Core;
using LordHelm.Monitor;

namespace LordHelm.Web;

public enum WidgetKind { Subprocess, Approval, Incident }

public sealed record WidgetModel(
    string Id,
    WidgetKind Kind,
    string Label,
    ExecutionEnvironment? Env,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Tail = null);

/// <summary>
/// Thread-safe view-model for the Blazor dashboard. Backend services push into here;
/// the <see cref="OnChanged"/> event drives Blazor re-renders via StateHasChanged.
/// </summary>
public sealed class WidgetState
{
    private readonly ConcurrentDictionary<string, WidgetModel> _widgets = new();
    public event Action? OnChanged;

    public IReadOnlyList<WidgetModel> Snapshot() =>
        _widgets.Values.OrderBy(w => w.UpdatedAt).ToList();

    public void Upsert(WidgetModel w)
    {
        _widgets[w.Id] = w;
        OnChanged?.Invoke();
    }

    public void Remove(string id)
    {
        _widgets.TryRemove(id, out _);
        OnChanged?.Invoke();
    }

    public void ApplyProcessEvent(ProcessEvent ev, LogRing? ring)
    {
        var env = ev.Label.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
            ? ExecutionEnvironment.Docker
            : ExecutionEnvironment.Host;
        var status = ev.Kind switch
        {
            ProcessEventKind.Started => "running",
            ProcessEventKind.Exited => ev.ExitCode == 0 ? "completed" : "failed",
            ProcessEventKind.Incident => "incident",
            _ => _widgets.TryGetValue(ev.SubprocessId, out var existing) ? existing.Status : "running",
        };
        Upsert(new WidgetModel(
            Id: ev.SubprocessId,
            Kind: WidgetKind.Subprocess,
            Label: ev.Label,
            Env: env,
            Status: status,
            UpdatedAt: ev.At,
            Tail: ring?.Snapshot()));
    }
}
