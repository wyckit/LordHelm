using System.Collections.Concurrent;
using System.Threading.Channels;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

/// <summary>
/// Default approval gate: publishes pending requests on a channel for the UI to consume,
/// supports a 60-second timeout-with-default-deny, and honours session-scoped batch tokens.
/// Auto-approves READ (tier ordering assumes Read is least risky).
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    private readonly IAuditLog _audit;
    private readonly ILogger<ApprovalGate> _logger;
    private readonly Channel<PendingApproval> _pending = Channel.CreateUnbounded<PendingApproval>();
    private readonly ConcurrentDictionary<string, BatchToken> _batchTokens = new();
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public ApprovalGate(IAuditLog audit, ILogger<ApprovalGate> logger)
    {
        _audit = audit;
        _logger = logger;
    }

    public ChannelReader<PendingApproval> PendingReader => _pending.Reader;

    public async Task<ApprovalDecision> RequestAsync(HostActionRequest req, CancellationToken ct = default)
    {
        if (req.RiskTier == RiskTier.Read)
        {
            await _audit.AppendAsync(req.SkillId, req.RiskTier.ToString(), "AutoApproved", req.OperatorId, req.SessionId, "tier=Read", ct);
            return new ApprovalDecision(true, "Auto-approved (Read)", DateTimeOffset.UtcNow, false);
        }

        if (_batchTokens.TryGetValue(req.SessionId, out var tok) && tok.IsUsable(req.RiskTier))
        {
            await _audit.AppendAsync(req.SkillId, req.RiskTier.ToString(), "BatchApproved", req.OperatorId, req.SessionId, "batch-token", ct);
            return new ApprovalDecision(true, "Batch token", DateTimeOffset.UtcNow, true);
        }

        var pending = new PendingApproval(req, new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
        await _pending.Writer.WriteAsync(pending, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);
        using var reg = timeoutCts.Token.Register(() => pending.Tcs.TrySetResult(
            new ApprovalDecision(false, "Timed out (default-deny)", DateTimeOffset.UtcNow, false)));

        var decision = await pending.Tcs.Task;
        await _audit.AppendAsync(req.SkillId, req.RiskTier.ToString(),
            decision.Approved ? "Approved" : "Denied",
            req.OperatorId, req.SessionId, decision.Reason, ct);
        return decision;
    }

    public void Resolve(PendingApproval pending, bool approved, string reason)
    {
        pending.Tcs.TrySetResult(new ApprovalDecision(approved, reason, DateTimeOffset.UtcNow, false));
    }

    public void GrantBatchToken(string sessionId, RiskTier maxTier, TimeSpan window)
    {
        _batchTokens[sessionId] = new BatchToken(maxTier, DateTimeOffset.UtcNow.Add(window));
    }

    public sealed record PendingApproval(HostActionRequest Request, TaskCompletionSource<ApprovalDecision> Tcs);

    private sealed record BatchToken(RiskTier MaxTier, DateTimeOffset ExpiresAt)
    {
        public bool IsUsable(RiskTier tier) =>
            DateTimeOffset.UtcNow < ExpiresAt && (int)tier <= (int)MaxTier;
    }
}
