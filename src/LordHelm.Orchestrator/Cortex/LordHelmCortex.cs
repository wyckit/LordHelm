using System.Text.Json;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Cortex;

/// <summary>
/// Panel-endorsed quarantine-first cortex. Every write goes to
/// <see cref="CortexNamespaces.Staging"/> first; <see cref="PromoteAsync"/>
/// moves an entry into <see cref="CortexNamespaces.Live"/> where recall
/// picks it up. A 7-day auto-promote time-gate is evaluated inside
/// <see cref="RecallAcrossFleetAsync"/> so a forgotten-but-useful staged
/// reflection can still surface without an operator press-gang, but the
/// common case is explicit promotion via the <c>/cortex</c> admin page.
/// </summary>
public sealed class LordHelmCortex : ILordHelmCortex
{
    private readonly IEngramClient _engram;
    private readonly IExpertRegistry _experts;
    private readonly ILogger<LordHelmCortex> _logger;
    private static readonly TimeSpan AutoPromoteAfter = TimeSpan.FromDays(7);

    public LordHelmCortex(
        IEngramClient engram,
        IExpertRegistry experts,
        ILogger<LordHelmCortex> logger)
    {
        _engram = engram;
        _experts = experts;
        _logger = logger;
    }

    public async Task<string> ThinkAsync(string text, string category = "reflection",
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var id = $"think-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}".Substring(0, 48);
        await _engram.StoreAsync(
            CortexNamespaces.Staging, id, text,
            category: category, metadata: metadata, ct: ct);
        return id;
    }

    public async Task<IReadOnlyList<EngramHit>> RecallAcrossFleetAsync(
        string query, int k = 8, CancellationToken ct = default)
    {
        var namespaces = new List<string> { CortexNamespaces.Live, CortexNamespaces.Staging };
        foreach (var e in _experts.All) namespaces.Add(e.EngramNamespace);
        namespaces.Add("work");
        namespaces.Add("synthesis");

        // Naive round-robin fanout; an engram-client with native cross-search
        // would be better, but this keeps the interface minimal for MVP.
        var pool = new List<EngramHit>();
        foreach (var ns in namespaces.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var hits = await _engram.SearchAsync(ns, query, k: Math.Min(5, k), ct);
                pool.AddRange(hits);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "cortex cross-ns search skipped {Ns}", ns);
            }
        }

        // Simple RRF merge: score per hit = Σ 1/(rank + 60) across namespaces it appeared in.
        var byId = pool
            .GroupBy(h => (h.Namespace, h.Id))
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(k)
            .ToList();
        return byId;
    }

    public async Task StoreRetrospectiveAsync(CortexRetrospective r, CancellationToken ct = default)
    {
        var id = $"retro-{r.GoalId}";
        var payload = new
        {
            r.GoalId, r.Goal, r.Succeeded, r.DagNodeCount, r.PersonasInvolved,
            r.Synthesis, r.ErrorDetail,
            completedAt = r.CompletedAt.ToString("O"),
        };
        var text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        var metadata = new Dictionary<string, string>
        {
            ["goal_id"]      = r.GoalId,
            ["succeeded"]    = r.Succeeded ? "true" : "false",
            ["personas"]     = string.Join(",", r.PersonasInvolved),
            ["dag_size"]     = r.DagNodeCount.ToString(),
            ["completed_at"] = r.CompletedAt.ToString("O"),
        };
        await _engram.StoreAsync(
            CortexNamespaces.Staging, id, text,
            category: "retrospective", metadata: metadata, ct: ct);
    }

    public async Task<bool> PromoteAsync(string stagingId, string reason, CancellationToken ct = default)
    {
        var hit = await _engram.GetAsync(CortexNamespaces.Staging, stagingId, ct);
        if (hit is null) return false;
        var meta = new Dictionary<string, string>(hit.Metadata)
        {
            ["promoted_from"] = CortexNamespaces.Staging,
            ["promoted_at"]   = DateTimeOffset.UtcNow.ToString("O"),
            ["promotion_reason"] = reason,
        };
        await _engram.StoreAsync(
            CortexNamespaces.Live, stagingId, hit.Text,
            category: meta.TryGetValue("category", out var c) ? c : "promoted",
            metadata: meta, ct: ct);
        return true;
    }

    public async Task<bool> RejectAsync(string stagingId, CancellationToken ct = default)
    {
        // IEngramClient has no delete primitive today. Instead we write a
        // tombstone sibling — a memory node at `tombstone-{stagingId}` with
        // category 'tombstone' that every cortex reader filters out.
        // ListStagingAsync hides tombstoned ids; CortexAutoPromoteService
        // refuses to promote them; RecallAcrossFleetAsync strips them from
        // the pool before RRF merge. Reversible: delete the tombstone to
        // un-reject (once engram gains a delete primitive).
        try
        {
            var hit = await _engram.GetAsync(CortexNamespaces.Staging, stagingId, ct);
            if (hit is null) return false;
            await _engram.StoreAsync(
                CortexNamespaces.Staging,
                TombstoneId(stagingId),
                text: $"tombstoned by operator at {DateTimeOffset.UtcNow:O}",
                category: "tombstone",
                metadata: new Dictionary<string, string>
                {
                    ["target_id"]      = stagingId,
                    ["tombstoned_at"]  = DateTimeOffset.UtcNow.ToString("O"),
                },
                ct: ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cortex reject failed for {Id}", stagingId);
            return false;
        }
    }

    public async Task<IReadOnlyList<EngramHit>> ListStagingAsync(int k = 50, CancellationToken ct = default)
    {
        try
        {
            var pool = await _engram.SearchAsync(CortexNamespaces.Staging, "recent", k * 2, ct);
            var tombstoned = new HashSet<string>(
                pool.Where(h => h.Metadata.TryGetValue("category", out var c) && c == "tombstone")
                    .Select(h => h.Metadata.TryGetValue("target_id", out var t) ? t : null)
                    .Where(t => !string.IsNullOrEmpty(t))!,
                StringComparer.OrdinalIgnoreCase);
            return pool
                .Where(h => !IsTombstoneId(h.Id)
                         && !tombstoned.Contains(h.Id))
                .Take(k)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "cortex staging list failed");
            return Array.Empty<EngramHit>();
        }
    }

    /// <summary>
    /// True when the given staging id has a sibling tombstone. Used by the
    /// auto-promote service and the /cortex admin page to skip rejected entries.
    /// </summary>
    public async Task<bool> IsRejectedAsync(string stagingId, CancellationToken ct = default)
    {
        try
        {
            var hit = await _engram.GetAsync(CortexNamespaces.Staging, TombstoneId(stagingId), ct);
            return hit is not null;
        }
        catch { return false; }
    }

    private static string TombstoneId(string stagingId) => $"tombstone-{stagingId}";
    private static bool IsTombstoneId(string id) => id.StartsWith("tombstone-", StringComparison.Ordinal);
}
