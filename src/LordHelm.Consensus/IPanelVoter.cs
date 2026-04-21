namespace LordHelm.Consensus;

public interface IPanelVoter
{
    string VoterId { get; }
    Task<PanelVote> VoteAsync(IncidentNode incident, string? dissentingRationale, CancellationToken ct = default);
}

public interface INoveltyCheck
{
    /// <summary>
    /// Returns false when the proposed fix is structurally identical to a prior escalated
    /// failure (prevents retry loops). Implementations typically embed the fix and compare
    /// cosine similarity against stored incident resolutions.
    /// </summary>
    Task<bool> IsNovelAsync(string proposedFix, CancellationToken ct = default);
}
