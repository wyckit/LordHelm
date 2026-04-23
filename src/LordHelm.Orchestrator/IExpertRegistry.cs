using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Orchestrator.Artifacts;
using LordHelm.Providers.Adapters;

namespace LordHelm.Orchestrator;

/// <summary>
/// Enumeration surface over every <see cref="IExpert"/> registered in the
/// process. Backed by <see cref="ExpertDirectory"/> so newly-registered
/// personas automatically produce a corresponding <see cref="IExpert"/> with
/// default <see cref="ExpertPolicy"/> + <see cref="ExpertBudget"/>. Operators
/// can override an expert's policy/budget via <see cref="Upsert"/>.
/// </summary>
public interface IExpertRegistry
{
    IReadOnlyList<IExpert> All { get; }
    IExpert? Get(string id);
    void Upsert(string personaId, ExpertPolicy policy, ExpertBudget budget);
    IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> GetOverrides();
    void ReplaceAll(IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> overrides);
    event Action? OnChanged;
}

public sealed class ExpertRegistry : IExpertRegistry
{
    private readonly ExpertDirectory _directory;
    private readonly IAdapterRouter _router;
    private readonly IEngramClient? _engram;
    private readonly IApprovalGate? _approvalGate;
    private readonly IArtifactStore? _artifacts;
    private readonly Dictionary<string, (ExpertPolicy policy, ExpertBudget budget)> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnChanged;

    public ExpertRegistry(
        ExpertDirectory directory,
        IAdapterRouter router,
        IEngramClient? engram = null,
        IApprovalGate? approvalGate = null,
        IArtifactStore? artifacts = null)
    {
        _directory = directory;
        _router = router;
        _engram = engram;
        _approvalGate = approvalGate;
        _artifacts = artifacts;
    }

    public IReadOnlyList<IExpert> All =>
        _directory.All().Select(p => (IExpert)Build(p)).ToList();

    public IExpert? Get(string id)
    {
        var p = _directory.Get(id);
        return p is null ? null : Build(p);
    }

    public void Upsert(string personaId, ExpertPolicy policy, ExpertBudget budget)
    {
        _overrides[personaId] = (policy, budget);
        OnChanged?.Invoke();
    }

    public IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> GetOverrides() =>
        _overrides.ToDictionary(kv => kv.Key, kv => (kv.Value.policy, kv.Value.budget), StringComparer.OrdinalIgnoreCase);

    public void ReplaceAll(IReadOnlyDictionary<string, (ExpertPolicy Policy, ExpertBudget Budget)> overrides)
    {
        _overrides.Clear();
        foreach (var kv in overrides)
            _overrides[kv.Key] = (kv.Value.Policy, kv.Value.Budget);
        OnChanged?.Invoke();
    }

    private Expert Build(ExpertPersona persona)
    {
        var (policy, budget) = _overrides.TryGetValue(persona.Id, out var o)
            ? o
            : (new ExpertPolicy(), new ExpertBudget());
        return new Expert(persona, policy, budget, _router, _engram, _approvalGate, _artifacts);
    }
}
