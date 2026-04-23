namespace LordHelm.Providers;

public sealed record VendorHealth(
    string VendorId,
    int InFlight,
    int WindowLimit,
    TimeSpan Window,
    bool IsHealthy,
    string? LastError = null,
    double RecentSuccessRate = 1.0,
    decimal RollingCostUsd = 0m,
    int RollingCallCount = 0,
    /// <summary>Cumulative real requests against this vendor's subscription
    /// since service start (populated by UsageAccumulator from adapter responses).
    /// Null when no real calls yet — the ribbon falls back to InFlight/WindowLimit.</summary>
    int? SubscriptionRequests = null,
    long? SubscriptionTokens = null,
    decimal? SubscriptionCostUsd = null,
    /// <summary>True when the last auth-probe OR a real call surfaced a
    /// quota/rate-limit signature. Router excludes this vendor from selection
    /// until the SubscriptionExhaustionMonitor confirms recovery.</summary>
    bool Exhausted = false,
    /// <summary>The model the vendor's auth probe actually resolved to —
    /// useful when operators want to confirm "yes, claude came back as
    /// claude-haiku-4-5 and not a stale default".</summary>
    string? ResolvedModel = null);

/// <summary>
/// Live rolling-window usage + health per vendor. Surfaces on the dashboard
/// summary ribbon and on the /providers page so operators can see the cost /
/// rate-limit state of every provider at a glance (Cummings supervisory-control
/// principle: real-time economic feedback is a prerequisite for rational tier
/// switching).
/// </summary>
public interface IProviderHealth
{
    IReadOnlyList<VendorHealth> GetAll();
    VendorHealth? Get(string vendorId);
}
