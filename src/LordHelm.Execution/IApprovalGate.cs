using LordHelm.Core;

namespace LordHelm.Execution;

public sealed record HostActionRequest(
    string SkillId,
    RiskTier RiskTier,
    string Summary,
    string? DiffPreview,
    string OperatorId,
    string SessionId);

public sealed record ApprovalDecision(
    bool Approved,
    string Reason,
    DateTimeOffset DecidedAt,
    bool UsedBatchToken);

public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(HostActionRequest req, CancellationToken ct = default);
    void GrantBatchToken(string sessionId, RiskTier maxTier, TimeSpan window);
}
