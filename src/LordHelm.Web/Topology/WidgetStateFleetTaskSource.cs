using LordHelm.Orchestrator.Topology;

namespace LordHelm.Web.Topology;

/// <summary>
/// Adapts <see cref="WidgetState"/> to the orchestrator-level
/// <see cref="IFleetTaskSource"/> contract so the projection service can
/// stay in <c>LordHelm.Orchestrator</c> (which doesn't know about Blazor).
/// Every widget becomes a <see cref="FleetTaskSnapshot"/>; the persona id
/// lives on widget labels today ("acting as {id}" log lines come from
/// GoalRunner) — for now we attribute via the widget id prefix
/// <c>expert-*</c> or a trailing <c>#m{index}</c> member tag, falling
/// back to null (unattributed) when we can't parse.
/// </summary>
public sealed class WidgetStateFleetTaskSource : IFleetTaskSource
{
    private readonly WidgetState _widgets;
    public event Action? OnChanged;

    public WidgetStateFleetTaskSource(WidgetState widgets)
    {
        _widgets = widgets;
        _widgets.OnChanged += () => OnChanged?.Invoke();
    }

    public IReadOnlyList<FleetTaskSnapshot> Snapshot() =>
        _widgets.Snapshot()
            .Select(w => new FleetTaskSnapshot(
                WidgetId: w.Id,
                Label: w.Label,
                Bucket: w.BucketOf(),
                Status: w.Status,
                OwnerPersona: ExtractPersona(w),
                UpdatedAt: w.UpdatedAt))
            .ToList();

    // GoalRunner sets widget ids for persona-run tasks to a stable shape
    // like "{goalId}::{memberTaskId}". It also emits log lines "acting as
    // {personaId} (ns=...)." — but parsing log lines is brittle, so we use
    // a simple heuristic: if the label starts with "persona via " we pull
    // the name that follows; otherwise look for a [persona] bracket tag
    // that several widgets stamp. Fallback: null (the widget affects only
    // helm load, not a specific agent node).
    private static string? ExtractPersona(WidgetModel w)
    {
        // Approval widgets carry "expert:{id}" on the SkillId which ends up in the label.
        if (w.Label.StartsWith("expert approval: ", StringComparison.Ordinal))
            return w.Label["expert approval: ".Length..];

        // GoalRunner swarm member labels look like "{goal} [persona via vendor #m0]".
        var open  = w.Label.IndexOf('[');
        var close = open >= 0 ? w.Label.IndexOf(" via ", open, StringComparison.Ordinal) : -1;
        if (open >= 0 && close > open)
            return w.Label.Substring(open + 1, close - open - 1).Trim();

        return null;
    }
}
