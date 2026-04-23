using System.Collections.Concurrent;

namespace LordHelm.Core;

/// <summary>
/// Snapshot of a vendor's subscription usage as reported by its CLI's
/// own /status or /usage slash command. All fields are nullable because
/// different vendors surface different fields. Volatile — lives in-memory;
/// the panel endorsed NOT mirroring every probe to engram (only the
/// command-shape reference lives there).
/// </summary>
public sealed record UsageSnapshot(
    string VendorId,
    int? RequestsUsed,
    int? RequestsLimit,
    long? TokensUsed,
    long? TokensLimit,
    decimal? CostUsd,
    DateTimeOffset? ResetAt,
    bool AuthOk,
    bool Exhausted,
    string? ResolvedModel,
    string? RawOutput,
    string? Error,
    DateTimeOffset ProbedAt);

/// <summary>
/// Thread-safe singleton holding the most recent UsageSnapshot per vendor.
/// Populated by <see cref="IUsageProbeService"/>, consumed by the
/// SummaryRibbon, <see cref="AdapterProviderOrchestrator.ToVendorHealth"/>,
/// and the /providers page. Fires <see cref="OnChanged"/> on every update
/// so Blazor surfaces can re-render without polling.
/// </summary>
public sealed class UsageState
{
    private readonly ConcurrentDictionary<string, UsageSnapshot> _byVendor =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnChanged;

    public UsageSnapshot? Get(string vendorId) =>
        _byVendor.TryGetValue(vendorId, out var s) ? s : null;

    public IReadOnlyDictionary<string, UsageSnapshot> Snapshot() =>
        _byVendor.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public void Update(UsageSnapshot snap)
    {
        _byVendor[snap.VendorId] = snap;
        OnChanged?.Invoke();
    }

    public void Clear()
    {
        _byVendor.Clear();
        OnChanged?.Invoke();
    }
}
