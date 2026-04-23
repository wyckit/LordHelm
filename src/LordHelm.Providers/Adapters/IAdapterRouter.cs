using LordHelm.Core;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Score-based adapter selection. Produces a ranked list of candidate
/// adapters for a given <see cref="RoutingRequest"/> using the seven-weight
/// function in <see cref="AdapterRoutingScore"/>. The top-ranked candidate
/// is returned from <see cref="PickAsync"/> unless the caller pinned a
/// specific vendor.
/// </summary>
public interface IAdapterRouter
{
    IReadOnlyList<AdapterRoutingScore> Rank(RoutingRequest request, string? pinnedVendor = null);

    IAgentModelAdapter? Pick(RoutingRequest request, string? pinnedVendor = null);

    IReadOnlyList<IAgentModelAdapter> Fallbacks(RoutingRequest request, string preferredVendor);
}

public sealed class AdapterRouter : IAdapterRouter
{
    private readonly IAgentAdapterRegistry _registry;
    private readonly IRouterWeights _weights;
    private readonly UsageState? _usage;

    public AdapterRouter(IAgentAdapterRegistry registry, IRouterWeights? weights = null, UsageState? usage = null)
    {
        _registry = registry;
        _weights = weights ?? new RouterWeightsProvider();
        _usage = usage;
    }

    public IReadOnlyList<AdapterRoutingScore> Rank(RoutingRequest request, string? pinnedVendor = null)
    {
        var weights = _weights.Current;
        // Panel-endorsed: vendors with Exhausted=true (last auth probe hit
        // subscription quota) are HARD-EXCLUDED from scoring — score-weight
        // deprioritisation is insufficient when the vendor is the only
        // remaining option. SubscriptionExhaustionMonitor will re-probe on
        // an escalating schedule; when recovery happens they re-enter the
        // pool on the next Rank call.
        var selectable = _registry.All
            .Where(a => !(_usage?.Get(a.VendorId)?.Exhausted ?? false))
            .ToList();
        if (selectable.Count == 0) selectable = _registry.All.ToList();  // better to fail a call than route nowhere

        var scored = selectable.Select(a => Score(a, request,
            effective: a.ResolveCapabilities(request.ModelHint ?? a.DefaultModel))).ToList();
        scored.Sort((l, r) => weights.Apply(r).CompareTo(weights.Apply(l)));

        if (pinnedVendor is not null)
        {
            var pin = scored.FirstOrDefault(s => s.VendorId.Equals(pinnedVendor, StringComparison.OrdinalIgnoreCase));
            if (pin is not null)
            {
                scored.Remove(pin);
                scored.Insert(0, pin);
            }
        }
        return scored;
    }

    public IAgentModelAdapter? Pick(RoutingRequest request, string? pinnedVendor = null)
    {
        var top = Rank(request, pinnedVendor).FirstOrDefault();
        return top is null ? null : _registry.Get(top.VendorId);
    }

    public IReadOnlyList<IAgentModelAdapter> Fallbacks(RoutingRequest request, string preferredVendor) =>
        Rank(request)
            .Where(s => !s.VendorId.Equals(preferredVendor, StringComparison.OrdinalIgnoreCase))
            .Select(s => _registry.Get(s.VendorId))
            .Where(a => a is not null)
            .Cast<IAgentModelAdapter>()
            .ToList();

    private static AdapterRoutingScore Score(IAgentModelAdapter adapter, RoutingRequest req, AdapterCapabilities? effective = null)
    {
        var caps = effective ?? adapter.Capabilities;
        var health = adapter.Health;

        var capabilityMatch = caps.SupportedTasks.Any(t => t.Equals(req.TaskKind, StringComparison.OrdinalIgnoreCase))
            ? 1.0
            : caps.SupportedTasks.Any(t => req.TaskKind.Contains(t, StringComparison.OrdinalIgnoreCase))
                ? 0.5
                : 0.0;

        // Blend EMA success with "currently healthy" — a flaky adapter that just
        // failed its last call is penalised even if its longer-term EMA is fine.
        var recentSuccess = health.IsHealthy
            ? health.RecentSuccessRate
            : Math.Min(health.RecentSuccessRate, 0.2);

        var latencyFit = caps.Mode switch
        {
            ResourceMode.Interactive => 1.0,
            ResourceMode.Builder => 0.7,
            ResourceMode.Batch => 0.4,
            _ => 0.5
        };

        var costFit = 1.0;
        if (req.MaxCostPerCallUsd is { } cap && cap > 0)
        {
            // crude envelope: assume avg call ~= 1k input / 1k output tokens
            var est = (double)(caps.Cost.InputPerMTokens + caps.Cost.OutputPerMTokens) / 1000.0;
            costFit = Math.Clamp(1.0 - (est / (double)cap), 0.0, 1.0);
        }
        else
        {
            // no explicit cap → rank cheaper adapters higher, but gently
            var total = (double)(caps.Cost.InputPerMTokens + caps.Cost.OutputPerMTokens);
            costFit = total <= 0 ? 1.0 : Math.Clamp(1.0 - Math.Log10(Math.Max(1.0, total)) / 3.0, 0.1, 1.0);
        }

        var contextFit = req.EstimatedContextTokens <= 0
            ? 1.0
            : caps.MaxContextTokens >= req.EstimatedContextTokens
                ? 1.0
                : Math.Clamp((double)caps.MaxContextTokens / req.EstimatedContextTokens, 0.0, 1.0);

        var toolFit = req.NeedsToolCalls
            ? (caps.SupportsToolCalls ? 1.0 : 0.1)
            : 1.0;

        var resourceFit = caps.Mode == req.PreferredMode ? 1.0 : 0.5;

        return new AdapterRoutingScore(
            adapter.VendorId,
            capabilityMatch,
            recentSuccess,
            latencyFit,
            costFit,
            contextFit,
            toolFit,
            resourceFit);
    }
}
