using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Consult;

public sealed class TieredConsultStrategy : IConsultStrategy
{
    private readonly IEngramClient? _engram;
    private readonly IExpertRegistry _experts;
    private readonly ILogger<TieredConsultStrategy> _logger;

    public TieredConsultStrategy(
        IExpertRegistry experts,
        ILogger<TieredConsultStrategy> logger,
        IEngramClient? engram = null)
    {
        _experts = experts;
        _logger = logger;
        _engram = engram;
    }

    public async Task<ConsultDecision> DecideAsync(ConsultRequest req, CancellationToken ct = default)
    {
        // ---- novelty short-circuit: is this task already in cortex? ----
        if (_engram is not null)
        {
            try
            {
                var query = req.Node.Goal;
                var hits = await _engram.SearchAsync(
                    "lord_helm_cortex", query, k: 3, ct);
                var best = hits.OrderByDescending(h => h.Score).FirstOrDefault();
                if (best is { Score: > 0.90, Text.Length: > 0 })
                {
                    _logger.LogInformation(
                        "Consult: novelty-check short-circuit for '{Goal}' (cosine {Score:0.00})",
                        req.Node.Goal, best.Score);
                    return new ConsultDecision(
                        Mode: ConsultMode.ShortCircuit,
                        Voters: 0,
                        Reason: $"cortex match {best.Id} @ {best.Score:0.00}",
                        CachedResolution: best.Text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Consult: novelty-check failed, falling through");
            }
        }

        // ---- panel-endorsed tier rules ----
        if (req.RiskTier >= RiskTier.Delete)
            return new ConsultDecision(ConsultMode.Consensus, 3, $"risk tier {req.RiskTier}");

        var kind = (req.Node.TaskKind ?? "").ToLowerInvariant();
        if (kind == "security" || req.Persona?.Id == "security-analyst")
            return new ConsultDecision(ConsultMode.Debate, 2, "security task");

        // Operator override: ExpertPolicy flag isn't present yet so we fall through;
        // the panel flagged this as a future extension.
        var policy = req.Persona is null ? null : _experts.Get(req.Persona.Id)?.Policy;
        if (policy?.RequiresApproval == true)
        {
            // RequiresApproval is a gate, not a debate trigger — noted but doesn't change mode here.
        }

        if (req.Node.EstimatedContextTokens > 40_000 &&
            kind is "research" or "reasoning")
            return new ConsultDecision(ConsultMode.Swarm, 2, "large-context diverse swarm");

        return new ConsultDecision(ConsultMode.Single, 1, "default tier");
    }
}
