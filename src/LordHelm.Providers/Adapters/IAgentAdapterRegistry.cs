using LordHelm.Core;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Enumerable surface over every registered <see cref="IAgentModelAdapter"/>.
/// Consumed by the (future) score-based router and by the dashboard
/// adapters page. Constructor-injectable — takes the DI-provided set of
/// adapters and indexes by vendor id.
/// </summary>
public interface IAgentAdapterRegistry
{
    IReadOnlyList<IAgentModelAdapter> All { get; }
    IAgentModelAdapter? Get(string vendorId);
}

public sealed class AgentAdapterRegistry : IAgentAdapterRegistry
{
    private readonly Dictionary<string, IAgentModelAdapter> _byVendor;

    public AgentAdapterRegistry(IEnumerable<IAgentModelAdapter> adapters)
    {
        _byVendor = adapters.ToDictionary(a => a.VendorId, StringComparer.OrdinalIgnoreCase);
        All = _byVendor.Values.ToList();
    }

    public IReadOnlyList<IAgentModelAdapter> All { get; }

    public IAgentModelAdapter? Get(string vendorId) =>
        _byVendor.TryGetValue(vendorId, out var a) ? a : null;
}
