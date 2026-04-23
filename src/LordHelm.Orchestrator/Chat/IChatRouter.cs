using LordHelm.Core;

namespace LordHelm.Orchestrator.Chat;

public enum ChatRouteKind
{
    /// <summary>Single expert answers directly. No decomposition.</summary>
    OneShot,
    /// <summary>Decomposer + GoalRunner + full DAG + synthesiser.</summary>
    DecomposeAndDispatch,
    /// <summary>Message is ambiguous — router asks the operator for clarification.</summary>
    Clarify,
    /// <summary>Router refuses (safety floor tripped, scope out of bounds).</summary>
    Refuse,
}

public sealed record ChatRoutingPlan(
    ChatRouteKind Kind,
    IReadOnlyList<string> PersonaHints,
    string? Tier,                     // "fast" | "deep" | "code" | null
    string? ModelHint,
    bool NeedsPanel,
    int PanelSize,
    IReadOnlyList<string> SkillHints,
    RiskTier? RiskTier,
    string Rationale,
    string? ClarifyingQuestion = null,
    string? RefusalReason = null);

public interface IChatRouter
{
    Task<ChatRoutingPlan> RouteAsync(
        string operatorMessage,
        IReadOnlyList<string> recentConversation,
        CancellationToken ct = default);
}
