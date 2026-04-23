using LordHelm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Usage;

public sealed class UsageProbeOptions
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>When true, runs a probe cycle at service startup to validate
    /// auth + catalog resolved models. Set false to skip in tests.</summary>
    public bool ProbeOnStartup { get; set; } = true;
}

/// <summary>
/// Drives the <see cref="IUsageProbe"/> set on a 5-minute cadence + on
/// startup + on manual refresh. Each probe populates <see cref="UsageState"/>
/// with auth status. Failure cases: probe returns Exhausted=true when
/// quota language is detected; router hard-excludes those vendors until the
/// next successful probe (escalating retry cadence is handled by
/// <see cref="SubscriptionExhaustionMonitor"/>).
/// </summary>
public sealed class UsageProbeService : BackgroundService
{
    private readonly IEnumerable<IUsageProbe> _probes;
    private readonly UsageState _state;
    private readonly UsageProbeOptions _options;
    private readonly ILogger<UsageProbeService> _logger;

    public UsageProbeService(
        IEnumerable<IUsageProbe> probes,
        UsageState state,
        UsageProbeOptions options,
        ILogger<UsageProbeService> logger)
    {
        _probes = probes;
        _state = state;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_options.ProbeOnStartup)
        {
            try { await RefreshAllAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "initial usage probe failed"); }
        }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.RefreshInterval, ct); }
            catch (OperationCanceledException) { break; }
            try { await RefreshAllAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "scheduled usage probe failed"); }
        }
    }

    /// <summary>Manual refresh entry — invoked by the dashboard refresh
    /// button + the <see cref="SubscriptionExhaustionMonitor"/> retry ticks.</summary>
    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAllAsync(CancellationToken ct = default)
    {
        // All three auth probes fire concurrently — cold-start worst case
        // drops from ~65s (sum of 20/20/25s per-vendor timeouts) to ~25s.
        var inFlight = _probes.Select(p => p.ProbeAsync(ct)).ToList();
        var snaps = await Task.WhenAll(inFlight);
        foreach (var snap in snaps)
        {
            _state.Update(snap);
            if (snap.AuthOk)
                _logger.LogInformation("usage probe {Vendor}: AuthOk", snap.VendorId);
            else
                _logger.LogInformation("usage probe {Vendor}: FAIL ({Err}) exhausted={Exhausted}",
                    snap.VendorId, snap.Error, snap.Exhausted);
        }
        return snaps;
    }
}

/// <summary>
/// Re-probes vendors that came back Exhausted on an escalating schedule so
/// we recover when the subscription re-credits. Per-vendor
/// NextRetryUtc, interval doubles up to a 6-hour cap then holds hourly
/// once recovered.
/// </summary>
public sealed class SubscriptionExhaustionMonitor : BackgroundService
{
    private readonly UsageState _state;
    private readonly UsageProbeService _probeService;
    private readonly ILogger<SubscriptionExhaustionMonitor> _logger;
    private readonly Dictionary<string, (DateTimeOffset NextRetry, TimeSpan Interval)> _retry =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan TickCadence = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RetrySchedule =
    {
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };

    public SubscriptionExhaustionMonitor(
        UsageState state,
        UsageProbeService probeService,
        ILogger<SubscriptionExhaustionMonitor> logger)
    {
        _state = state;
        _probeService = probeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "exhaustion-monitor tick failed"); }
            try { await Task.Delay(TickCadence, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = _state.Snapshot();
        foreach (var (vendor, snap) in snapshots)
        {
            if (!snap.Exhausted)
            {
                _retry.Remove(vendor);
                continue;
            }

            var (nextRetry, interval) = _retry.TryGetValue(vendor, out var info)
                ? info
                : (now + RetrySchedule[0], RetrySchedule[0]);

            if (now < nextRetry)
            {
                _retry[vendor] = (nextRetry, interval);
                continue;
            }

            _logger.LogInformation("exhaustion-monitor: re-probing {Vendor} (last interval {Min}min)",
                vendor, (int)interval.TotalMinutes);
            var results = await _probeService.RefreshAllAsync(ct);
            var refreshed = results.FirstOrDefault(r => string.Equals(r.VendorId, vendor, StringComparison.OrdinalIgnoreCase));
            if (refreshed?.AuthOk == true)
            {
                _retry.Remove(vendor);
                _logger.LogInformation("exhaustion-monitor: {Vendor} recovered", vendor);
                continue;
            }

            // Still exhausted — step up the schedule.
            var idx = Array.IndexOf(RetrySchedule, interval);
            var nextInterval = idx < 0 || idx >= RetrySchedule.Length - 1
                ? RetrySchedule[^1]
                : RetrySchedule[idx + 1];
            _retry[vendor] = (now + nextInterval, nextInterval);
        }
    }
}
