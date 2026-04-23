namespace LordHelm.Providers.Adapters;

/// <summary>
/// Seven weights used by <see cref="AdapterRouter"/> to collapse an
/// <see cref="AdapterRoutingScore"/> into a single ranking number. Defaults
/// match the panel-endorsed design (0.30 / 0.20 / 0.10 × 5). Operators can
/// bias the router by writing to <c>data/routing-weights.json</c> or editing
/// via the <c>/routing</c> page.
/// </summary>
public sealed record RouterWeights(
    double CapabilityMatch = 0.30,
    double RecentSuccess   = 0.20,
    double LatencyFit      = 0.10,
    double CostFit         = 0.10,
    double ContextFit      = 0.10,
    double ToolFit         = 0.10,
    double ResourceFit     = 0.10)
{
    public static RouterWeights Default { get; } = new();

    public double Apply(AdapterRoutingScore s) =>
        s.CapabilityMatch * CapabilityMatch +
        s.RecentSuccess   * RecentSuccess   +
        s.LatencyFit      * LatencyFit      +
        s.CostFit         * CostFit         +
        s.ContextFit      * ContextFit      +
        s.ToolFit         * ToolFit         +
        s.ResourceFit     * ResourceFit;
}

/// <summary>
/// Mutable singleton holding the current router weights. Emits
/// <see cref="OnChanged"/> on <see cref="Replace"/> so a file-persistence
/// service can save to disk and downstream consumers can refresh.
/// </summary>
public interface IRouterWeights
{
    RouterWeights Current { get; }
    void Replace(RouterWeights weights);
    event Action? OnChanged;
}

public sealed class RouterWeightsProvider : IRouterWeights
{
    private RouterWeights _current = RouterWeights.Default;
    public event Action? OnChanged;

    public RouterWeights Current => _current;

    public void Replace(RouterWeights weights)
    {
        _current = weights;
        OnChanged?.Invoke();
    }
}
