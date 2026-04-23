using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Chat;

public sealed class ChatDispatcher : IChatDispatcher
{
    private readonly IChatRouter _router;
    private readonly SafetyFloor _floor;
    private readonly IGoalRunner _runner;
    private readonly IExpertRegistry _experts;
    private readonly ILogger<ChatDispatcher> _logger;

    public ChatDispatcher(
        IChatRouter router,
        SafetyFloor floor,
        IGoalRunner runner,
        IExpertRegistry experts,
        ILogger<ChatDispatcher> logger)
    {
        _router = router;
        _floor = floor;
        _runner = runner;
        _experts = experts;
        _logger = logger;
    }

    public async Task<ChatDispatchResult> DispatchAsync(ChatDispatchRequest req, CancellationToken ct = default)
    {
        // Router + floor — unless the caller (e.g. a pre-parsed API request)
        // asks to skip. Skipping still enforces the safety floor defaults.
        ChatRoutingPlan plan;
        if (req.SkipRouter)
        {
            plan = new ChatRoutingPlan(
                Kind: ChatRouteKind.DecomposeAndDispatch,
                PersonaHints: Array.Empty<string>(),
                Tier: req.ExplicitTier,
                ModelHint: req.ExplicitModel,
                NeedsPanel: false, PanelSize: 0,
                SkillHints: Array.Empty<string>(),
                RiskTier: null,
                Rationale: "router skipped (caller supplied explicit hints)");
        }
        else
        {
            plan = await _router.RouteAsync(req.Text, req.RecentConversation ?? Array.Empty<string>(), ct);
            plan = _floor.Apply(plan);
        }

        // Clarify/Refuse short-circuit — no goal runs.
        if (plan.Kind == ChatRouteKind.Clarify)
            return new ChatDispatchResult(plan, null,
                plan.ClarifyingQuestion ?? "Can you clarify the request?", Halted: true);
        if (plan.Kind == ChatRouteKind.Refuse)
            return new ChatDispatchResult(plan, null,
                "Refused: " + (plan.RefusalReason ?? "out of scope"), Halted: true);

        // Goal dispatch — PreferredVendor from first persona hint unless explicit.
        var preferredVendor = req.ExplicitVendor
            ?? (plan.PersonaHints.Count > 0 ? _experts.Get(plan.PersonaHints[0])?.Persona.PreferredVendor : null);
        var model = req.ExplicitModel ?? plan.ModelHint;

        // Persona fallback — when the decomposer leaves a node's persona
        // unset, use the router's first persona hint, then synthesiser as a
        // last resort. Without a concrete persona the widget labels tag
        // "expert" (a literal) and the Fleet Roster can't attribute the work
        // back to anyone, so every expert reads Idle while tasks run.
        var defaultPersona = plan.PersonaHints.Count > 0
            ? plan.PersonaHints[0]
            : (_experts.Get("productivity-copilot") is not null ? "productivity-copilot" : "synthesiser");

        var result = await _runner.RunAsync(new GoalRunRequest(
            Goal: req.Text,
            PreferredVendor: preferredVendor,
            Model: model,
            SessionId: req.SessionId,
            Thinking: req.Thinking,
            DefaultPersona: defaultPersona), ct);

        var reply = !string.IsNullOrWhiteSpace(result.Synthesis) ? result.Synthesis!
                    : result.NodeOutputs.Count == 1 ? result.NodeOutputs.First().Value
                    : result.ErrorDetail ?? "(no output)";

        return new ChatDispatchResult(plan, result, reply.Trim(), Halted: false);
    }
}
