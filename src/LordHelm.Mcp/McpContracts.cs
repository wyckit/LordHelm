using LordHelm.Core;

namespace LordHelm.Mcp;

public sealed record DispatchGoalRequest(string Goal, int Priority = 0, string? SessionId = null);

public sealed record DispatchGoalResponse(
    string GoalId,
    bool Accepted,
    string? Reason,
    int DagNodeCount);

public sealed record SkillSummary(string Id, string Version, ExecutionEnvironment Env, RiskTier RiskTier);

public sealed record IncidentSummary(
    string IncidentId,
    string SkillId,
    int ExitCode,
    DateTimeOffset At,
    bool Resolved);

public sealed record ExpertSummary(string ExpertId, string VendorId, string Model);

public interface ILordHelmMcpServer
{
    Task<DispatchGoalResponse> DispatchGoalAsync(DispatchGoalRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<SkillSummary>> ListSkillsAsync(CancellationToken ct = default);
    Task<IncidentSummary?> GetIncidentAsync(string incidentId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpertSummary>> ListExpertsAsync(CancellationToken ct = default);
}
