using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.ModelDiscovery;

public sealed class ModelCatalogRefresherOptions
{
    /// <summary>How often to re-probe. Defaults to 6 hours — long enough not to
    /// spam the CLIs, short enough that new vendor releases show up same-day.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>If true, run a probe cycle immediately on startup before waiting.</summary>
    public bool ProbeOnStartup { get; set; } = true;

    /// <summary>
    /// When true (default), a successful probe REMOVES catalog entries for that
    /// vendor that the CLI no longer reports — so the /models dropdown tracks
    /// reality. Set to false to revert to mark-as-unavailable history-keeping
    /// behaviour.
    /// </summary>
    public bool PruneStaleOnProbe { get; set; } = true;

    /// <summary>
    /// Kept for backwards-compat callers that still flip the old flag.
    /// Only consulted when <see cref="PruneStaleOnProbe"/> is false.
    /// </summary>
    public bool MarkStaleAsUnavailable { get; set; } = true;
}

/// <summary>
/// Periodic hosted service that runs every registered <see cref="IModelProber"/>
/// and merges results into <see cref="IModelCatalog"/> via
/// <see cref="IModelCatalog.Upsert"/>. Each merge preserves operator-tuned
/// capability fields (context/cost/mode) on already-catalogued models —
/// only the volatile fields (Description, IsAvailable, LastProbed) are
/// refreshed from the probe output.
/// </summary>
public sealed class ModelCatalogRefresher : BackgroundService
{
    private readonly IEnumerable<IModelProber> _probers;
    private readonly IModelCatalog _catalog;
    private readonly ModelCatalogRefresherOptions _options;
    private readonly ILogger<ModelCatalogRefresher> _logger;

    public ModelCatalogRefresher(
        IEnumerable<IModelProber> probers,
        IModelCatalog catalog,
        ModelCatalogRefresherOptions options,
        ILogger<ModelCatalogRefresher> logger)
    {
        _probers = probers;
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_options.ProbeOnStartup)
        {
            try { await RefreshAllAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial model refresh failed"); }
        }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.Interval, ct); }
            catch (OperationCanceledException) { break; }
            try { await RefreshAllAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Scheduled model refresh failed"); }
        }
    }

    public async Task<IReadOnlyList<ProbeResult>> RefreshAllAsync(CancellationToken ct = default)
    {
        // All three probers run concurrently. Cuts cold-start model
        // discovery from ~3× per-vendor latency down to 1×.
        var probes = _probers.Select(p => p.ProbeAsync(ct)).ToList();
        var results = await Task.WhenAll(probes);
        foreach (var result in results)
        {
            if (result.Succeeded) MergeProbe(result);
        }
        return results;
    }

    /// <summary>
    /// Single-vendor refresh — the unit event-driven triggers ask for.
    /// Used by ScoutMutation → "this CLI just changed" and UsageState
    /// transitions → "this vendor just authenticated". Per-vendor probes
    /// are debounced internally so a burst of events collapses to one probe.
    /// </summary>
    public async Task<ProbeResult?> RefreshVendorAsync(string vendorId, CancellationToken ct = default)
    {
        var prober = _probers.FirstOrDefault(p =>
            string.Equals(p.VendorId, vendorId, StringComparison.OrdinalIgnoreCase));
        if (prober is null)
        {
            _logger.LogDebug("RefreshVendorAsync: no prober for {Vendor}", vendorId);
            return null;
        }
        if (!_perVendorDebounce.TryEnter(vendorId)) return null; // suppressed by debounce
        try
        {
            var result = await prober.ProbeAsync(ct);
            if (result.Succeeded) MergeProbe(result);
            _logger.LogInformation(
                "Event-driven refresh {Vendor}: {Status} ({N} models)",
                vendorId, result.Succeeded ? "OK" : "FAIL", result.Models.Count);
            return result;
        }
        finally { _perVendorDebounce.Release(vendorId); }
    }

    private readonly PerVendorDebounce _perVendorDebounce = new(TimeSpan.FromSeconds(30));

    /// <summary>Caps event-driven per-vendor refreshes to one per debounce
    /// window so a burst of Scout mutations or auth-state flips doesn't
    /// fan out into a probe storm.</summary>
    private sealed class PerVendorDebounce
    {
        private readonly TimeSpan _window;
        private readonly Dictionary<string, DateTimeOffset> _lastEntered = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        public PerVendorDebounce(TimeSpan window) { _window = window; }
        public bool TryEnter(string vendorId)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_lastEntered.TryGetValue(vendorId, out var last) && now - last < _window) return false;
                _lastEntered[vendorId] = now;
                return true;
            }
        }
        public void Release(string vendorId) { /* timestamp-based, no release needed */ }
    }

    private void MergeProbe(ProbeResult result)
    {
        var now = DateTimeOffset.UtcNow;
        var probedIds = new HashSet<string>(
            result.Models.Select(m => m.ModelId),
            StringComparer.OrdinalIgnoreCase);

        // Upsert every probed model, preserving operator-tuned capability fields.
        var existingByKey = _catalog.GetModels(result.VendorId)
            .ToDictionary(e => e.ModelId, e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var m in result.Models)
        {
            var existing = existingByKey.TryGetValue(m.ModelId, out var e) ? e : null;
            _catalog.Upsert(new ModelEntry(
                VendorId: result.VendorId,
                ModelId: m.ModelId,
                Tier: existing?.Tier ?? m.InferredTier,
                Description: string.IsNullOrWhiteSpace(m.Description)
                    ? (existing?.Description ?? "")
                    : m.Description,
                IsAvailable: true,
                LastProbed: now,
                MaxContextTokens: existing?.MaxContextTokens,
                InputPerMTokens: existing?.InputPerMTokens,
                OutputPerMTokens: existing?.OutputPerMTokens,
                SupportsToolCalls: existing?.SupportsToolCalls,
                Mode: existing?.Mode));
        }

        // Stale = existing entry for this vendor that the CLI didn't return.
        // Default: prune it so the UI matches the CLI. Fallback: mark unavailable.
        var stale = existingByKey.Values.Where(e => !probedIds.Contains(e.ModelId)).ToList();
        if (_options.PruneStaleOnProbe)
        {
            foreach (var s in stale)
                _catalog.Remove(s.VendorId, s.ModelId);
        }
        else if (_options.MarkStaleAsUnavailable)
        {
            foreach (var s in stale.Where(e => e.IsAvailable))
                _catalog.MarkAvailability(s.VendorId, s.ModelId, false);
        }
    }
}
