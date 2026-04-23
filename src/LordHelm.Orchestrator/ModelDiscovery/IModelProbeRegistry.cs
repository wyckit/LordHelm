namespace LordHelm.Orchestrator.ModelDiscovery;

/// <summary>
/// Mutable registry of per-vendor probe specs. Operators edit specs from
/// <c>/models/probes</c>; changes fire <see cref="OnChanged"/> so the
/// persistence layer can save to <c>data/model-probes.json</c>.
/// </summary>
public interface IModelProbeRegistry
{
    IReadOnlyList<ModelProbeSpec> All { get; }
    ModelProbeSpec? Get(string vendorId);
    void Upsert(ModelProbeSpec spec);
    void ReplaceAll(IEnumerable<ModelProbeSpec> specs);
    event Action? OnChanged;
}

public sealed class ModelProbeRegistry : IModelProbeRegistry
{
    private readonly Dictionary<string, ModelProbeSpec> _specs = new(StringComparer.OrdinalIgnoreCase);
    public event Action? OnChanged;

    public ModelProbeRegistry(IEnumerable<ModelProbeSpec>? seed = null)
    {
        foreach (var s in seed ?? ModelProberDefaults.Defaults())
            _specs[s.VendorId] = s;
    }

    public IReadOnlyList<ModelProbeSpec> All => _specs.Values.OrderBy(s => s.VendorId).ToList();

    public ModelProbeSpec? Get(string vendorId) =>
        _specs.TryGetValue(vendorId, out var s) ? s : null;

    public void Upsert(ModelProbeSpec spec)
    {
        _specs[spec.VendorId] = spec;
        OnChanged?.Invoke();
    }

    public void ReplaceAll(IEnumerable<ModelProbeSpec> specs)
    {
        _specs.Clear();
        foreach (var s in specs) _specs[s.VendorId] = s;
        OnChanged?.Invoke();
    }
}
