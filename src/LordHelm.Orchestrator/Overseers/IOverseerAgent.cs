using LordHelm.Core;

namespace LordHelm.Orchestrator.Overseers;

public enum OverseerStatus
{
    /// <summary>Agent has more work to do; reschedule on the standard cadence.</summary>
    Working,
    /// <summary>Agent is idle but still watching; reschedule on a longer cadence.</summary>
    WaitingForAttention,
    /// <summary>Agent believes nothing needs doing right now; pause until manually re-enabled or bumped.</summary>
    DoneForNow,
    /// <summary>Last tick failed; runner applies exponential backoff.</summary>
    Error,
}

public sealed record OverseerContext(
    IAlertTray AlertTray,
    IServiceProvider Services,
    int TickNumber,
    DateTimeOffset At);

public sealed record OverseerResult(
    OverseerStatus Status,
    string? Message = null,
    /// <summary>Override the next tick interval (e.g. request an early wake-up).</summary>
    TimeSpan? NextIntervalOverride = null);

/// <summary>
/// An overseer is a background agent with an opinion about when it should run.
/// The runner wakes it up on <see cref="DefaultInterval"/>; the agent can:
///   - return <see cref="OverseerStatus.DoneForNow"/> to pause itself until re-enabled or bumped;
///   - return <see cref="OverseerResult.NextIntervalOverride"/> to request a sooner (or later) next tick;
///   - push alerts via <see cref="OverseerContext.AlertTray"/> to surface attention in the UI.
/// External events can call <see cref="OverseerRegistry.Bump"/> to force an immediate tick
/// regardless of the current schedule.
/// </summary>
public interface IOverseerAgent
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    TimeSpan DefaultInterval { get; }

    Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct);
}
