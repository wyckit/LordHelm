using LordHelm.Core;

namespace LordHelm.Orchestrator.Consult;

public enum ConsultMode { Single, Swarm, Debate, Consensus, ShortCircuit }

public sealed record ConsultDecision(
    ConsultMode Mode,
    int Voters,
    string Reason,
    string? CachedResolution = null);   // populated when Mode=ShortCircuit

public sealed record ConsultRequest(
    string GoalId,
    TaskNode Node,
    ExpertPersona? Persona,
    RiskTier RiskTier = RiskTier.Read,
    IReadOnlyList<string>? RecentFailureCategories = null);

/// <summary>
/// Decides how a task should be consulted — single expert, swarm, debate,
/// consensus — and, via novelty-check, whether the whole thing can be
/// short-circuited because an identical problem was already resolved.
/// Panel-endorsed tiered model (session debate-lordhelm-swarm-cortex-
/// projection-2026-04-21):
///
///   RiskTier ≥ Delete                           → Consensus (3 voters)
///   TaskKind = security OR persona = security-  → Debate (2 voters)
///     analyst
///   EstimatedContextTokens > 40k AND             → Swarm (2 diverse)
///     TaskKind ∈ {research, reasoning}
///   ExpertPolicy.AlwaysDebate = true            → Debate (2 voters)
///   novelty-check hit (cosine ≥ 0.9)            → ShortCircuit
///   default                                      → Single
///
/// Novelty-check runs before the tier check — if a similar past resolution
/// exists, we return it without convening a panel.
/// </summary>
public interface IConsultStrategy
{
    Task<ConsultDecision> DecideAsync(ConsultRequest request, CancellationToken ct = default);
}
