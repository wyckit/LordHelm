using System.Collections.Concurrent;
using LordHelm.Core;
using LordHelm.Orchestrator;

namespace LordHelm.Mcp;

public sealed class LordHelmMcpServer : ILordHelmMcpServer
{
    private readonly ILordHelmManager _manager;
    private readonly Func<IReadOnlyList<SkillManifest>> _skillsProvider;
    private readonly Func<IReadOnlyList<ExpertSummary>> _expertsProvider;
    private readonly ConcurrentDictionary<string, IncidentSummary> _incidents = new();

    public LordHelmMcpServer(
        ILordHelmManager manager,
        Func<IReadOnlyList<SkillManifest>> skillsProvider,
        Func<IReadOnlyList<ExpertSummary>>? expertsProvider = null)
    {
        _manager = manager;
        _skillsProvider = skillsProvider;
        _expertsProvider = expertsProvider ?? (() => Array.Empty<ExpertSummary>());
    }

    public async Task<DispatchGoalResponse> DispatchGoalAsync(DispatchGoalRequest req, CancellationToken ct = default)
    {
        var goalId = Guid.NewGuid().ToString("N");
        var skills = _skillsProvider();
        var result = await _manager.RunAsync(req.Goal, skills,
            node => Task.FromResult($"(stub) executed {node.Id}: {node.Goal}"), ct);
        return new DispatchGoalResponse(goalId, result.Succeeded, result.ErrorDetail, result.Dag.Count);
    }

    public Task<IReadOnlyList<SkillSummary>> ListSkillsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SkillSummary> list = _skillsProvider()
            .Select(s => new SkillSummary(s.Id, s.Version.ToString(), s.ExecEnv, s.RiskTier))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IncidentSummary?> GetIncidentAsync(string incidentId, CancellationToken ct = default)
    {
        _incidents.TryGetValue(incidentId, out var inc);
        return Task.FromResult(inc);
    }

    public Task<IReadOnlyList<ExpertSummary>> ListExpertsAsync(CancellationToken ct = default) =>
        Task.FromResult(_expertsProvider());

    public void RecordIncident(IncidentSummary incident) => _incidents[incident.IncidentId] = incident;
}
