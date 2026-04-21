using System.Text.Json;
using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Skills;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record ProvisionRequest(
    string ExpertId,
    string SkillId,
    string CliVendorId,
    string Model,
    string Goal,
    string? ArgsJson = null,
    string? Persona = null,
    string? SystemHint = null);

public sealed record ProvisionedExpert(ExpertProfile Profile, ExpertRunner Run);

public delegate Task<string> ExpertRunner(CancellationToken ct);

/// <summary>
/// Builds an <see cref="ExpertProfile"/> for a sub-task, resolves the required skill
/// from <see cref="ISkillCache"/>, composes an <see cref="ExpertRunner"/> closure that
/// routes through <see cref="IExecutionRouter"/>, and optionally specialises the
/// expert with a persona pulled from <see cref="ExpertDirectory"/>. This is the
/// concrete "Dynamic Assembly" step of the Think Tank Workflow (spec §4.2).
/// </summary>
public interface IExpertProvisioner
{
    Task<ProvisionedExpert?> ProvisionAsync(ProvisionRequest req, CancellationToken ct = default);
}

public sealed class DefaultExpertProvisioner : IExpertProvisioner
{
    private readonly ISkillCache _skills;
    private readonly IExecutionRouter _router;
    private readonly ExpertDirectory _directory;
    private readonly ILogger<DefaultExpertProvisioner> _logger;
    private readonly string _defaultShell;

    public DefaultExpertProvisioner(
        ISkillCache skills,
        IExecutionRouter router,
        ExpertDirectory directory,
        ILogger<DefaultExpertProvisioner> logger,
        string? defaultShell = null)
    {
        _skills = skills;
        _router = router;
        _directory = directory;
        _logger = logger;
        _defaultShell = defaultShell ?? (OperatingSystem.IsWindows() ? "PowerShell" : "Bash");
    }

    public async Task<ProvisionedExpert?> ProvisionAsync(ProvisionRequest req, CancellationToken ct = default)
    {
        var skill = await _skills.GetByIdAsync(req.SkillId, ct);
        if (skill is null)
        {
            _logger.LogWarning("Expert {Expert} requested skill {Skill} which is not in the cache.", req.ExpertId, req.SkillId);
            return null;
        }

        // Resolve persona from directory if one is named.
        var persona = req.Persona is null ? null : _directory.Get(req.Persona);
        var vendor = persona?.PreferredVendor ?? req.CliVendorId;
        var model = persona?.Model ?? req.Model;

        var profile = new ExpertProfile(
            ExpertId: persona is null ? req.ExpertId : $"{persona.Id}::{req.ExpertId}",
            CliVendorId: vendor,
            Model: model,
            SkillLoadout: persona?.PreferredSkills.Concat(new[] { req.SkillId }).Distinct().ToArray()
                ?? new[] { req.SkillId },
            GoalContext: req.Goal);

        var shell = Enum.TryParse<TargetShell>(_defaultShell, out var parsed) ? parsed : TargetShell.Bash;

        ExpertRunner runner = async token =>
        {
            using var args = JsonDocument.Parse(req.ArgsJson ?? "{}");
            var result = await _router.RouteAsync(skill, args, profile, cliVersion: "*", shell, token);
            return result.Approved
                ? $"exit={result.ExitCode}\n{result.Stdout}"
                : $"denied: {result.Stderr}";
        };

        return new ProvisionedExpert(profile, runner);
    }
}
