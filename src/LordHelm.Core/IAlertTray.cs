namespace LordHelm.Core;

public enum AlertKind
{
    /// <summary>General informational update — no action required.</summary>
    Info,
    /// <summary>Warning — possible issue, review when convenient.</summary>
    Warning,
    /// <summary>Attention required — operator should look at this soon.</summary>
    Attention,
    /// <summary>An agent has finished its pending work and is pausing.</summary>
    DoneForNow,
    /// <summary>An agent failed unexpectedly.</summary>
    Error,
}

public sealed record AlertEntry(
    string Id,
    string Source,
    AlertKind Kind,
    string Title,
    string Body,
    DateTimeOffset At,
    bool Read = false);

/// <summary>
/// Central notification channel for overseer agents and any other long-running
/// service that needs to surface events to the operator. Kept in <c>LordHelm.Core</c>
/// so every layer can publish without taking a dependency on the concrete
/// implementation. UI layer subscribes to <see cref="OnChanged"/>.
/// </summary>
public interface IAlertTray
{
    event Action? OnChanged;

    Task<AlertEntry> PushAsync(string source, AlertKind kind, string title, string body, CancellationToken ct = default);
    IReadOnlyList<AlertEntry> All();
    int UnreadCount { get; }
    Task MarkReadAsync(string id, CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
