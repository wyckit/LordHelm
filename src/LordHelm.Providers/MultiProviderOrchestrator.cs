using LordHelm.Core;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging;

namespace LordHelm.Providers;

public sealed record ProviderConfig(
    string VendorId,
    string DefaultModel,
    RateLimitGovernor Governor,
    IAgentOutcomeModelClient Client,
    int Priority = 0);

public enum FailoverPolicy { RoundRobin, PriorityWeighted, Disabled }

public interface IProviderOrchestrator
{
    IReadOnlyList<string> VendorIds { get; }
    Task<ProviderResponse> GenerateAsync(string vendorId, string? modelOverride, string prompt, int maxTokens = 512, float temperature = 0.1f, CancellationToken ct = default);
    Task<ProviderResponse> GenerateWithFailoverAsync(string preferredVendor, string? modelOverride, string prompt, int maxTokens = 512, float temperature = 0.1f, CancellationToken ct = default);
}

public sealed class MultiProviderOrchestrator : IProviderOrchestrator, IProviderHealth
{
    private readonly Dictionary<string, ProviderConfig> _providers;
    private readonly FailoverPolicy _policy;
    private readonly ILogger<MultiProviderOrchestrator> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _lastErrors = new();
    private int _rrIndex;

    public MultiProviderOrchestrator(IEnumerable<ProviderConfig> providers, FailoverPolicy policy, ILogger<MultiProviderOrchestrator> logger)
    {
        _providers = providers.ToDictionary(p => p.VendorId, StringComparer.OrdinalIgnoreCase);
        _policy = policy;
        _logger = logger;
    }

    public IReadOnlyList<string> VendorIds => _providers.Keys.ToList();

    public IReadOnlyList<VendorHealth> GetAll() =>
        _providers.Values.Select(BuildHealth).ToList();

    public VendorHealth? Get(string vendorId) =>
        _providers.TryGetValue(vendorId, out var p) ? BuildHealth(p) : null;

    private VendorHealth BuildHealth(ProviderConfig p)
    {
        var err = _lastErrors.TryGetValue(p.VendorId, out var e) ? e : null;
        return new VendorHealth(
            VendorId: p.VendorId,
            InFlight: p.Governor.InFlight,
            WindowLimit: p.Governor.MaxCallsPerWindow,
            Window: p.Governor.Window,
            IsHealthy: err is null,
            LastError: err);
    }

    public async Task<ProviderResponse> GenerateAsync(string vendorId, string? modelOverride, string prompt, int maxTokens = 512, float temperature = 0.1f, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(vendorId, out var p))
            return new ProviderResponse(string.Empty, Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0), new ErrorRecord("unknown_vendor", vendorId));

        await p.Governor.WaitAsync(ct);
        try
        {
            var model = modelOverride ?? p.DefaultModel;
            var text = await p.Client.GenerateAsync(model, prompt, maxTokens, temperature, ct);
            if (text is null)
            {
                _lastErrors[vendorId] = "null_response";
                return new ProviderResponse(string.Empty, Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0), new ErrorRecord("null_response", $"{vendorId} returned null"));
            }
            _lastErrors[vendorId] = null;
            return new ProviderResponse(text, Array.Empty<ToolCall>(), EstimateUsage(prompt, text), null);
        }
        catch (Exception ex)
        {
            _lastErrors[vendorId] = ex.Message;
            _logger.LogWarning(ex, "Provider {Vendor} failed", vendorId);
            return new ProviderResponse(string.Empty, Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0), new ErrorRecord("exception", ex.Message));
        }
    }

    public async Task<ProviderResponse> GenerateWithFailoverAsync(string preferredVendor, string? modelOverride, string prompt, int maxTokens = 512, float temperature = 0.1f, CancellationToken ct = default)
    {
        var first = await GenerateAsync(preferredVendor, modelOverride, prompt, maxTokens, temperature, ct);
        if (first.Error is null) return first;
        if (_policy == FailoverPolicy.Disabled) return first;

        var candidates = _policy == FailoverPolicy.PriorityWeighted
            ? _providers.Values.Where(p => !p.VendorId.Equals(preferredVendor, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Priority).ToList()
            : _providers.Values.Where(p => !p.VendorId.Equals(preferredVendor, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_policy == FailoverPolicy.RoundRobin && candidates.Count > 0)
        {
            var start = Interlocked.Increment(ref _rrIndex);
            candidates = Enumerable.Range(0, candidates.Count).Select(i => candidates[(start + i) % candidates.Count]).ToList();
        }

        foreach (var c in candidates)
        {
            var r = await GenerateAsync(c.VendorId, null, prompt, maxTokens, temperature, ct);
            if (r.Error is null) return r;
        }
        return first;
    }

    private static UsageRecord EstimateUsage(string prompt, string output) =>
        new(Math.Max(1, prompt.Length / 4), Math.Max(1, output.Length / 4), 0);
}
