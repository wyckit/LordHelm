using LordHelm.Core;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Pure scoring of a single <see cref="IAgentModelAdapter"/> against a
/// <see cref="RoutingRequest"/>. The seven weights sum to 1.00 and match the
/// panel-endorsed design:
///   capability_match 0.30  +  recent_success 0.20
/// + latency_fit     0.10  +  cost_fit        0.10
/// + context_fit     0.10  +  tool_fit        0.10
/// + resource_fit    0.10
/// The router composing these scores is a future slice — this record is the
/// stable contract that slice will depend on.
/// </summary>
public sealed record AdapterRoutingScore(
    string VendorId,
    double CapabilityMatch,
    double RecentSuccess,
    double LatencyFit,
    double CostFit,
    double ContextFit,
    double ToolFit,
    double ResourceFit)
{
    public double Total =>
        CapabilityMatch * 0.30 +
        RecentSuccess   * 0.20 +
        LatencyFit      * 0.10 +
        CostFit         * 0.10 +
        ContextFit      * 0.10 +
        ToolFit         * 0.10 +
        ResourceFit     * 0.10;
}

public sealed record RoutingRequest(
    string TaskKind,
    int EstimatedContextTokens,
    bool NeedsToolCalls,
    ResourceMode PreferredMode,
    decimal? MaxCostPerCallUsd = null,
    string? ModelHint = null);
