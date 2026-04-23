using LordHelm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.ModelDiscovery;

/// <summary>
/// Event-driven trigger: when a vendor's auth status flips from failing to
/// OK (e.g. operator just installed/configured the CLI, or a transient
/// network failure cleared), kick off a single-vendor model refresh.
/// Pairs with the timer-based <see cref="ModelCatalogRefresher"/> so the
/// catalog reacts to real signals instead of waiting for the next 6h tick.
/// </summary>
public sealed class UsageStateModelRefreshTrigger : IHostedService, IDisposable
{
    private readonly UsageState _usage;
    private readonly ModelCatalogRefresher _refresher;
    private readonly ILogger<UsageStateModelRefreshTrigger> _logger;
    private readonly Dictionary<string, bool> _lastAuthOk = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public UsageStateModelRefreshTrigger(
        UsageState usage,
        ModelCatalogRefresher refresher,
        ILogger<UsageStateModelRefreshTrigger> logger)
    {
        _usage = usage;
        _refresher = refresher;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Seed the baseline so the first OnChanged after startup doesn't
        // fire a refresh just because we're reading state for the first time.
        SeedBaseline();
        _usage.OnChanged += HandleChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _usage.OnChanged -= HandleChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => _usage.OnChanged -= HandleChanged;

    private void SeedBaseline()
    {
        lock (_gate)
            foreach (var (vendor, snap) in _usage.Snapshot())
                _lastAuthOk[vendor] = snap.AuthOk;
    }

    private void HandleChanged()
    {
        // Diff per-vendor auth state vs the last snapshot we observed.
        // Trigger refresh only on FALSE → TRUE transitions; ignore continual
        // "still OK" or "still failing" cases. Per-vendor refresh is
        // debounced inside the refresher itself, so spurious flips are safe.
        List<string> recovered = new();
        lock (_gate)
        {
            foreach (var (vendor, snap) in _usage.Snapshot())
            {
                var was = _lastAuthOk.TryGetValue(vendor, out var prev) && prev;
                if (snap.AuthOk && !was) recovered.Add(vendor);
                _lastAuthOk[vendor] = snap.AuthOk;
            }
        }
        foreach (var v in recovered)
        {
            _logger.LogInformation("Auth recovered for {Vendor} — triggering model refresh", v);
            _ = Task.Run(async () =>
            {
                try { await _refresher.RefreshVendorAsync(v); }
                catch (Exception ex) { _logger.LogWarning(ex, "auth-recovered model refresh failed for {Vendor}", v); }
            });
        }
    }
}
