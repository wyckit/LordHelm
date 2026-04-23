namespace LordHelm.Orchestrator.Topology;

/// <summary>
/// Abstraction over whatever layer tracks live widget/task activity (today
/// <c>LordHelm.Web.WidgetState</c>). The projection service reads this on
/// every change event and rolls load / status into the matching
/// <see cref="TopologyNode"/>. Kept abstract so <c>LordHelm.Orchestrator</c>
/// does not have to take a Web-layer dependency.
/// </summary>
public interface IFleetTaskSource
{
    IReadOnlyList<FleetTaskSnapshot> Snapshot();
    event Action? OnChanged;
}

public sealed record FleetTaskSnapshot(
    string WidgetId,
    string Label,
    string Bucket,         // "Incident" | "Approval" | "Running" | "Completed" | "Failed"
    string Status,
    string? OwnerPersona,  // persona id that owns this task, null if unattributed
    DateTimeOffset UpdatedAt);
