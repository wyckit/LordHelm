using LordHelm.Core;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Drop-in replacement for the retired <c>MultiProviderOrchestrator</c>. Speaks
/// the legacy <see cref="IProviderOrchestrator"/> + <see cref="IProviderHealth"/>
/// shape so GoalRunner, the LLM decomposer/aggregator/synthesiser, and the
/// /providers page continue to work unchanged — but routing now goes through
/// <see cref="IAdapterRouter"/> over <see cref="IAgentAdapterRegistry"/>.
/// </summary>
public sealed class AdapterProviderOrchestrator : IProviderOrchestrator, IProviderHealth
{
    private readonly IAgentAdapterRegistry _registry;
    private readonly IAdapterRouter _router;
    private readonly UsageState? _usage;

    public AdapterProviderOrchestrator(IAgentAdapterRegistry registry, IAdapterRouter router, UsageState? usage = null)
    {
        _registry = registry;
        _router = router;
        _usage = usage;
    }

    public IReadOnlyList<string> VendorIds => _registry.All.Select(a => a.VendorId).ToList();

    public IReadOnlyList<VendorHealth> GetAll() =>
        _registry.All.Select(ToVendorHealth).ToList();

    public VendorHealth? Get(string vendorId)
    {
        var a = _registry.Get(vendorId);
        return a is null ? null : ToVendorHealth(a);
    }

    public async Task<ProviderResponse> GenerateAsync(
        string vendorId,
        string? modelOverride,
        string prompt,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default)
    {
        var adapter = _registry.Get(vendorId);
        if (adapter is null)
            return new ProviderResponse(string.Empty, Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0),
                new ErrorRecord("unknown_vendor", vendorId));

        var resp = await adapter.GenerateAsync(
            new AgentRequest(prompt, modelOverride, maxTokens, temperature), ct);
        return resp.Response;
    }

    public Task<ProviderResponse> GenerateWithFailoverAsync(
        string preferredVendor,
        string? modelOverride,
        string prompt,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default) =>
        GenerateWithFailoverAsync(preferredVendor, modelOverride, prompt,
            new ProviderTaskHint(TaskKind: null,
                EstimatedContextTokens: Math.Max(1, prompt.Length / 4)),
            maxTokens, temperature, ct);

    public async Task<ProviderResponse> GenerateWithFailoverAsync(
        string preferredVendor,
        string? modelOverride,
        string prompt,
        ProviderTaskHint hint,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default)
    {
        var first = await GenerateAsync(preferredVendor, modelOverride, prompt, maxTokens, temperature, ct);
        if (first.Error is null) return first;

        var routingReq = new RoutingRequest(
            TaskKind: hint.TaskKind ?? "reasoning",
            EstimatedContextTokens: hint.EstimatedContextTokens > 0
                ? hint.EstimatedContextTokens
                : Math.Max(1, prompt.Length / 4),
            NeedsToolCalls: hint.NeedsToolCalls,
            PreferredMode: hint.PreferredMode,
            ModelHint: modelOverride);

        foreach (var candidate in _router.Fallbacks(routingReq, preferredVendor))
        {
            var resp = await candidate.GenerateAsync(
                new AgentRequest(prompt, ModelOverride: null, maxTokens, temperature), ct);
            if (resp.Response.Error is null) return resp.Response;
        }
        return first;
    }

    private VendorHealth ToVendorHealth(IAgentModelAdapter a)
    {
        var h = a.Health;
        var snap = _usage?.Get(a.VendorId);
        return new VendorHealth(
            VendorId: a.VendorId,
            InFlight: h.InFlight,
            WindowLimit: h.WindowLimit,
            Window: h.Window,
            IsHealthy: h.IsHealthy && (snap?.AuthOk ?? true),
            LastError: h.LastError ?? snap?.Error,
            RecentSuccessRate: h.RecentSuccessRate,
            RollingCostUsd: h.RollingCostUsd,
            RollingCallCount: h.RollingCallCount,
            SubscriptionRequests: snap?.RequestsUsed,
            SubscriptionTokens: snap?.TokensUsed,
            SubscriptionCostUsd: snap?.CostUsd,
            Exhausted: snap?.Exhausted ?? false,
            ResolvedModel: snap?.ResolvedModel);
    }
}
