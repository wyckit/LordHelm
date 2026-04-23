namespace LordHelm.Orchestrator.Chat;

public sealed record ChatDispatchRequest(
    string Text,
    string? SessionId = null,
    IReadOnlyList<string>? RecentConversation = null,
    string? ExplicitVendor = null,
    string? ExplicitTier = null,
    string? ExplicitModel = null,
    bool SkipRouter = false,
    /// <summary>Operator toggle from the orch panel. When true, GoalRunner
    /// wraps every LLM prompt in the DAG with a reasoning preamble so the
    /// model shows its thinking. Ignored by models that don't benefit
    /// (the UI only shows the toggle for reasoning-capable models).</summary>
    bool Thinking = false);

public sealed record ChatDispatchResult(
    ChatRoutingPlan Plan,
    GoalRunResult? GoalResult,
    string ReplyText,
    bool Halted);

/// <summary>
/// Single entry point every dispatch surface routes through — HelmChat,
/// the Throne above the grid, the <c>/api/goals</c> endpoint, any future
/// voice/slack/ingest adapter. Guarantees the operator message flows
/// through IChatRouter + SafetyFloor before reaching GoalRunner, so
/// routing decisions and safety escalations stay consistent regardless
/// of which UI surface submitted the goal.
/// </summary>
public interface IChatDispatcher
{
    Task<ChatDispatchResult> DispatchAsync(ChatDispatchRequest req, CancellationToken ct = default);
}
