using LordHelm.Core;

namespace LordHelm.Orchestrator.Cortex;

/// <summary>
/// Lord Helm's central cortex — the brain of the fleet. Writes land in
/// <c>lord_helm_cortex_staging</c> and promote to <c>lord_helm_cortex</c>
/// only after operator approval or the time-gate. Recall spans cortex +
/// every <c>expert_*</c> namespace + shared <c>work</c>/<c>synthesis</c>,
/// so the synthesiser, decomposer, and HelmChat all get cross-fleet memory.
/// </summary>
public interface ILordHelmCortex
{
    /// <summary>Store a reflection/thought to staging. Returns the entry id.</summary>
    Task<string> ThinkAsync(string text, string category = "reflection",
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Panel-endorsed central call: RRF-merged cross-namespace recall.
    /// Searches cortex + cortex_staging + every known expert_* namespace.</summary>
    Task<IReadOnlyList<EngramHit>> RecallAcrossFleetAsync(string query, int k = 8,
        CancellationToken ct = default);

    /// <summary>Append a retrospective for a completed goal run — pure structural
    /// data, no LLM call. Runs in GoalRunner.finally.</summary>
    Task StoreRetrospectiveAsync(CortexRetrospective retrospective,
        CancellationToken ct = default);

    /// <summary>Promote a staged entry to the live cortex namespace.</summary>
    Task<bool> PromoteAsync(string stagingId, string reason, CancellationToken ct = default);

    /// <summary>Reject and delete a staged entry.</summary>
    Task<bool> RejectAsync(string stagingId, CancellationToken ct = default);

    /// <summary>List entries currently in staging (awaiting operator or time-gate).
    /// Tombstoned entries are filtered out.</summary>
    Task<IReadOnlyList<EngramHit>> ListStagingAsync(int k = 50, CancellationToken ct = default);

    /// <summary>True if a tombstone sibling exists for this staging id — the
    /// auto-promote service checks this before promoting.</summary>
    Task<bool> IsRejectedAsync(string stagingId, CancellationToken ct = default);
}

public sealed record CortexRetrospective(
    string GoalId,
    string Goal,
    bool Succeeded,
    int DagNodeCount,
    IReadOnlyList<string> PersonasInvolved,
    string? Synthesis,
    string? ErrorDetail,
    DateTimeOffset CompletedAt);

public static class CortexNamespaces
{
    public const string Live    = "lord_helm_cortex";
    public const string Staging = "lord_helm_cortex_staging";
}
