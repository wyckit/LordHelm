using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Chat;

/// <summary>
/// Applies non-negotiable safety rules on top of an LLM-proposed routing
/// plan. Can ONLY escalate (more rigor) — never de-escalate. Panel-endorsed
/// floor (session debate-lordhelm-chat-as-dispatch-llm-routed-2026-04-21):
///
///   RiskTier ≥ Delete     →  force Consensus (3 voters minimum)
///   RiskTier ≥ Network    →  force operator approval regardless
///   Any RequiresApproval  →  force operator approval regardless
///   ExpertPolicy.         →  force Debate (2 voters minimum)
///     AlwaysDebate
///
/// The caller logs the override so operators can audit router-vs-floor
/// disagreements.
/// </summary>
public sealed class SafetyFloor
{
    private readonly IExpertRegistry _experts;
    private readonly ILogger<SafetyFloor> _logger;

    public SafetyFloor(IExpertRegistry experts, ILogger<SafetyFloor> logger)
    {
        _experts = experts;
        _logger = logger;
    }

    public ChatRoutingPlan Apply(ChatRoutingPlan plan)
    {
        var adjusted = plan;
        var overrides = new List<string>();

        // ---- Delete/Exec → mandatory consensus panel ----
        if (plan.RiskTier is RiskTier.Delete or RiskTier.Exec)
        {
            var size = Math.Max(plan.PanelSize, 3);
            if (!plan.NeedsPanel || plan.PanelSize < 3)
            {
                adjusted = adjusted with { NeedsPanel = true, PanelSize = size };
                overrides.Add($"forced panel(size={size}) for risk={plan.RiskTier}");
            }
        }

        // ---- AlwaysDebate persona → mandatory debate panel ----
        foreach (var p in plan.PersonaHints)
        {
            var expert = _experts.Get(p);
            if (expert is null) continue;
            if (expert.Policy.AlwaysDebate && (!plan.NeedsPanel || plan.PanelSize < 2))
            {
                var size = Math.Max(adjusted.PanelSize, 2);
                adjusted = adjusted with { NeedsPanel = true, PanelSize = size };
                overrides.Add($"forced debate for always-debate persona {p}");
                break;
            }
            if (expert.Policy.RequiresApproval && !plan.NeedsPanel)
            {
                var size = Math.Max(adjusted.PanelSize, 2);
                adjusted = adjusted with { NeedsPanel = true, PanelSize = size };
                overrides.Add($"forced panel for requires-approval persona {p}");
                break;
            }
        }

        // ---- Refuse-on-floor — the LLM can't approve a refusal escape ----
        // (no override here yet; rule reserved for a later slice)

        if (overrides.Count > 0)
            _logger.LogInformation("SafetyFloor escalations: {Overrides}", string.Join("; ", overrides));

        return adjusted;
    }

    /// <summary>
    /// True when a routing plan requires operator approval regardless of
    /// what the router said. Caller routes the task through ApprovalGate
    /// before the expert act runs.
    /// </summary>
    public bool RequiresOperatorGate(ChatRoutingPlan plan) =>
        plan.RiskTier is RiskTier.Network or RiskTier.Delete or RiskTier.Exec;
}
