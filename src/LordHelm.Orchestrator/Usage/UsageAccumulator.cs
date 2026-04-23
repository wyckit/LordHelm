using LordHelm.Core;

namespace LordHelm.Orchestrator.Usage;

/// <summary>
/// Bridges <see cref="UsageState"/> with the real per-call usage the adapter
/// sees on every successful response. Implements <see cref="IUsageReporter"/>
/// so AdapterBase can report without importing Orchestrator. Maintains
/// cumulative tokens + request count + rolling cost per vendor so the
/// SummaryRibbon can show REAL numbers instead of governor estimates.
/// Research agent (2026-04-21) flagged this as THE path: no CLI exposes
/// a /status endpoint, so we accumulate client-side.
/// </summary>
public sealed class UsageAccumulator : IUsageReporter
{
    private readonly UsageState _state;
    private readonly Dictionary<string, Totals> _totals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public UsageAccumulator(UsageState state) { _state = state; }

    public void Report(string vendorId, string modelId, UsageRecord usage, decimal costUsd) =>
        Record(vendorId, modelId, usage, costUsd);

    public void Record(string vendorId, string modelId, UsageRecord usage, decimal costUsd)
    {
        if (string.IsNullOrWhiteSpace(vendorId)) return;
        Totals t;
        lock (_gate)
        {
            if (!_totals.TryGetValue(vendorId, out t!))
            {
                t = new Totals();
                _totals[vendorId] = t;
            }
            t.Requests++;
            t.TokensIn  += usage.InputTokens;
            t.TokensOut += usage.OutputTokens;
            t.CostUsd   += costUsd;
            t.LastModel = modelId;
        }

        var existing = _state.Get(vendorId);
        UsageSnapshot merged;
        if (existing is null)
        {
            merged = new UsageSnapshot(
                VendorId: vendorId,
                RequestsUsed: t.Requests,
                RequestsLimit: null,
                TokensUsed: t.TokensIn + t.TokensOut,
                TokensLimit: null,
                CostUsd: t.CostUsd,
                ResetAt: null,
                AuthOk: true,
                Exhausted: false,
                ResolvedModel: modelId,
                RawOutput: null,
                Error: null,
                ProbedAt: DateTimeOffset.UtcNow);
        }
        else
        {
            merged = existing with
            {
                RequestsUsed = t.Requests,
                TokensUsed = t.TokensIn + t.TokensOut,
                CostUsd = t.CostUsd,
                ResolvedModel = modelId,
                ProbedAt = DateTimeOffset.UtcNow,
            };
        }
        _state.Update(merged);
    }

    public void Reset(string vendorId)
    {
        lock (_gate) _totals.Remove(vendorId);
    }

    private sealed class Totals
    {
        public int Requests;
        public long TokensIn;
        public long TokensOut;
        public decimal CostUsd;
        public string? LastModel;
    }
}
