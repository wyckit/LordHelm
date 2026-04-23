using System.Diagnostics;
using LordHelm.Core;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Shared adapter plumbing: governor-guarded dispatch, health bookkeeping,
/// exception → <see cref="ErrorRecord"/> normalization. Concrete adapters
/// supply the vendor id, default model, capability shape, and the underlying
/// <see cref="IAgentOutcomeModelClient"/> subprocess client.
/// </summary>
public abstract class AdapterBase : IAgentModelAdapter
{
    private readonly IAgentOutcomeModelClient _cli;
    private readonly RateLimitGovernor _governor;
    private readonly AdapterCapabilities _baseline;
    private readonly IModelCapabilityProvider? _catalog;
    private readonly object _successGate = new();
    private readonly object _costGate = new();
    private readonly Queue<(DateTimeOffset at, decimal usd)> _costs = new();
    private static readonly TimeSpan _costWindow = TimeSpan.FromHours(1);
    private string? _lastError;
    private DateTimeOffset _lastProbe = DateTimeOffset.UtcNow;
    private double _successEma = 1.0; // start optimistic; first failure pulls it down
    private const double _successAlpha = 0.2;

    private readonly IUsageReporter? _usageReporter;

    protected AdapterBase(
        string vendorId,
        string defaultModel,
        AdapterCapabilities capabilities,
        IAgentOutcomeModelClient cli,
        RateLimitGovernor governor,
        IModelCapabilityProvider? catalog = null,
        IUsageReporter? usageReporter = null)
    {
        VendorId = vendorId;
        DefaultModel = defaultModel;
        _baseline = capabilities;
        _cli = cli;
        _governor = governor;
        _catalog = catalog;
        _usageReporter = usageReporter;
    }

    public string VendorId { get; }
    public string DefaultModel { get; }

    /// <summary>
    /// Effective capabilities for this adapter at its <see cref="DefaultModel"/>.
    /// Overlays the baseline with any non-null fields from
    /// <see cref="IModelCapabilityProvider.TryGet"/> so different model ids
    /// under the same vendor can carry different context sizes / costs.
    /// </summary>
    public AdapterCapabilities Capabilities => ResolveCapabilities(DefaultModel);

    public AdapterCapabilities ResolveCapabilities(string modelId)
    {
        var overrides = _catalog?.TryGet(VendorId, modelId);
        if (overrides is null) return _baseline;
        var cost = (overrides.InputPerMTokens is not null || overrides.OutputPerMTokens is not null)
            ? new CostProfile(
                InputPerMTokens: overrides.InputPerMTokens ?? _baseline.Cost.InputPerMTokens,
                OutputPerMTokens: overrides.OutputPerMTokens ?? _baseline.Cost.OutputPerMTokens,
                CacheReadPerMTokens: _baseline.Cost.CacheReadPerMTokens)
            : _baseline.Cost;
        return _baseline with
        {
            MaxContextTokens = overrides.MaxContextTokens ?? _baseline.MaxContextTokens,
            SupportsToolCalls = overrides.SupportsToolCalls ?? _baseline.SupportsToolCalls,
            Mode = overrides.Mode ?? _baseline.Mode,
            Cost = cost,
        };
    }

    public AdapterHealth Health
    {
        get
        {
            double ema;
            lock (_successGate) ema = _successEma;
            var (usd, count) = GetRollingCost();
            return new AdapterHealth(
                InFlight: _governor.InFlight,
                WindowLimit: _governor.MaxCallsPerWindow,
                Window: _governor.Window,
                IsHealthy: _lastError is null,
                RecentSuccessRate: ema,
                LastError: _lastError,
                LastProbeUtc: _lastProbe,
                RollingCostUsd: usd,
                RollingCallCount: count,
                RollingWindow: _costWindow);
        }
    }

    private void RecordOutcome(bool success)
    {
        lock (_successGate)
            _successEma = _successAlpha * (success ? 1.0 : 0.0) + (1.0 - _successAlpha) * _successEma;
    }

    private decimal CostFor(string modelId, UsageRecord usage)
    {
        var cost = ResolveCapabilities(modelId).Cost;
        return
            ((decimal)usage.InputTokens  / 1_000_000m) * cost.InputPerMTokens  +
            ((decimal)usage.OutputTokens / 1_000_000m) * cost.OutputPerMTokens +
            ((decimal)usage.CacheReadTokens / 1_000_000m) * cost.CacheReadPerMTokens;
    }

    private void RecordCostLocal(decimal usd)
    {
        if (usd <= 0) return;
        lock (_costGate)
        {
            _costs.Enqueue((DateTimeOffset.UtcNow, usd));
            TrimLocked();
        }
    }

    private (decimal usd, int count) GetRollingCost()
    {
        lock (_costGate)
        {
            TrimLocked();
            return (_costs.Sum(e => e.usd), _costs.Count);
        }
    }

    private void TrimLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - _costWindow;
        while (_costs.Count > 0 && _costs.Peek().at < cutoff) _costs.Dequeue();
    }

    public async Task<AgentResponse> GenerateAsync(AgentRequest request, CancellationToken ct = default)
    {
        var model = request.ModelOverride ?? DefaultModel;
        var prompt = ComposePrompt(request);

        await _governor.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var text = await _cli.GenerateAsync(model, prompt, request.MaxTokens, request.Temperature, ct);
            sw.Stop();
            _lastProbe = DateTimeOffset.UtcNow;

            if (text is null)
            {
                _lastError = "null_response";
                RecordOutcome(false);
                return Fail(model, sw.Elapsed, "null_response", $"{VendorId} returned null");
            }
            _lastError = null;
            RecordOutcome(true);
            var usage = EstimateUsage(prompt, text);
            var costUsd = CostFor(model, usage);
            RecordCostLocal(costUsd);
            _usageReporter?.Report(VendorId, model, usage, costUsd);
            var pr = new ProviderResponse(text, Array.Empty<ToolCall>(), usage, null);
            return new AgentResponse(VendorId, model, pr, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _lastError = ex.Message;
            _lastProbe = DateTimeOffset.UtcNow;
            RecordOutcome(false);
            return Fail(model, sw.Elapsed, "exception", ex.Message);
        }
    }

    private AgentResponse Fail(string model, TimeSpan elapsed, string code, string message) => new(
        VendorId, model,
        new ProviderResponse(string.Empty, Array.Empty<ToolCall>(), new UsageRecord(0, 0, 0), new ErrorRecord(code, message)),
        elapsed);

    private static string ComposePrompt(AgentRequest r) =>
        string.IsNullOrWhiteSpace(r.SystemHint) ? r.Prompt : $"{r.SystemHint}\n\n{r.Prompt}";

    private static UsageRecord EstimateUsage(string prompt, string output) =>
        new(Math.Max(1, prompt.Length / 4), Math.Max(1, output.Length / 4), 0);
}
