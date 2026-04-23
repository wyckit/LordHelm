using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Cortex;

/// <summary>
/// Periodic time-gate for cortex_staging → cortex_live. Panel-endorsed
/// guardrail (session debate-lordhelm-swarm-cortex-projection-2026-04-21):
/// reflections sit in staging for up to 7 days before they're auto-promoted
/// if the operator hasn't reviewed them. This balances "never let an
/// unreviewed insight enter the cortex forever" with "stale-but-useful
/// context shouldn't be lost to operator inattention".
///
/// Ticks once an hour. For each staged entry whose produced-at stamp is
/// older than <see cref="AutoPromoteAfter"/>, calls ILordHelmCortex.PromoteAsync
/// with reason="auto-promote:time-gate". Errors are logged and skipped —
/// the service never throws or exits on bad data.
/// </summary>
public sealed class CortexAutoPromoteService : BackgroundService
{
    private readonly ILordHelmCortex _cortex;
    private readonly ILogger<CortexAutoPromoteService> _logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan AutoPromoteAfter = TimeSpan.FromDays(7);

    public CortexAutoPromoteService(ILordHelmCortex cortex, ILogger<CortexAutoPromoteService> logger)
    {
        _cortex = cortex;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PromoteReadyAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "cortex auto-promote tick failed"); }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PromoteReadyAsync(CancellationToken ct)
    {
        // ListStagingAsync already strips tombstoned ids; IsRejectedAsync is
        // a defensive second check in case a tombstone landed between list
        // and promote.
        var staged = await _cortex.ListStagingAsync(k: 200, ct);
        var cutoff = DateTimeOffset.UtcNow - AutoPromoteAfter;
        var promoted = 0;
        var skippedRejected = 0;
        foreach (var hit in staged)
        {
            var producedAt = ExtractProducedAt(hit);
            if (producedAt is null || producedAt > cutoff) continue;
            if (await _cortex.IsRejectedAsync(hit.Id, ct)) { skippedRejected++; continue; }

            var ok = await _cortex.PromoteAsync(hit.Id, reason: "auto-promote:time-gate", ct);
            if (ok) promoted++;
        }
        if (promoted > 0 || skippedRejected > 0)
            _logger.LogInformation("cortex auto-promote: {Promoted} advanced, {Skipped} skipped (rejected)",
                promoted, skippedRejected);
    }

    private static DateTimeOffset? ExtractProducedAt(LordHelm.Core.EngramHit hit)
    {
        // Retrospectives carry completed_at; ThinkAsync ids embed a unix-ms prefix.
        if (hit.Metadata.TryGetValue("completed_at", out var iso) &&
            DateTimeOffset.TryParse(iso, out var parsed))
            return parsed;
        if (hit.Id.StartsWith("think-", StringComparison.Ordinal))
        {
            var parts = hit.Id.Split('-');
            if (parts.Length > 1 && long.TryParse(parts[1], out var unixMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        return null;
    }
}
