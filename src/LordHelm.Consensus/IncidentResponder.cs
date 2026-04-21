using System.Text.Json;
using LordHelm.Core;
using LordHelm.Monitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Consensus;

/// <summary>
/// Connects the Watcher -> Consensus pipeline (spec §5): consumes <see cref="IProcessMonitor.Events"/>,
/// converts failed exits and explicit incident events into <see cref="IncidentNode"/> records,
/// stores them to engram namespace <c>lord_helm_incidents</c>, invokes
/// <see cref="IConsensusProtocol.ResolveAsync"/>, and writes the resolution back to engram
/// (or escalates to a human operator on deadlock).
/// </summary>
public sealed class IncidentResponder : BackgroundService
{
    private readonly IProcessMonitor _monitor;
    private readonly IConsensusProtocol _consensus;
    private readonly IEngramClient _engram;
    private readonly ILogger<IncidentResponder> _logger;

    public IncidentResponder(
        IProcessMonitor monitor,
        IConsensusProtocol consensus,
        IEngramClient engram,
        ILogger<IncidentResponder> logger)
    {
        _monitor = monitor;
        _consensus = consensus;
        _engram = engram;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var pending = new Dictionary<string, (string Label, List<string> Stdout, List<string> Stderr)>();

        await foreach (var ev in _monitor.Events.ReadAllAsync(ct))
        {
            switch (ev.Kind)
            {
                case ProcessEventKind.Started:
                    pending[ev.SubprocessId] = (ev.Label, new List<string>(), new List<string>());
                    break;
                case ProcessEventKind.Stdout when ev.Line is not null:
                    if (pending.TryGetValue(ev.SubprocessId, out var sOut)) sOut.Stdout.Add(ev.Line);
                    break;
                case ProcessEventKind.Stderr when ev.Line is not null:
                    if (pending.TryGetValue(ev.SubprocessId, out var sErr)) sErr.Stderr.Add(ev.Line);
                    break;
                case ProcessEventKind.Exited when ev.ExitCode is int code && code != 0:
                case ProcessEventKind.Incident:
                    if (pending.TryGetValue(ev.SubprocessId, out var buf))
                    {
                        await HandleIncidentAsync(ev, buf.Label, buf.Stdout, buf.Stderr, ct);
                        pending.Remove(ev.SubprocessId);
                    }
                    else
                    {
                        await HandleIncidentAsync(ev, ev.Label, new List<string>(), new List<string>(), ct);
                    }
                    break;
                case ProcessEventKind.Exited:
                    pending.Remove(ev.SubprocessId);
                    break;
            }
        }
    }

    private async Task HandleIncidentAsync(ProcessEvent ev, string label, List<string> stdout, List<string> stderr, CancellationToken ct)
    {
        var incident = new IncidentNode(
            IncidentId: $"inc-{ev.SubprocessId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            SkillId: label,
            ArgsHash: ev.SubprocessId,
            ExitCode: ev.ExitCode ?? -1,
            Stdout: string.Join('\n', stdout.TakeLast(64)),
            Stderr: string.Join('\n', stderr.TakeLast(64)),
            At: ev.At);

        _logger.LogWarning("Incident detected: {Id} skill={Skill} exit={Exit}", incident.IncidentId, incident.SkillId, incident.ExitCode);

        await _engram.StoreAsync(
            "lord_helm_incidents",
            incident.IncidentId,
            JsonSerializer.Serialize(incident),
            category: "incident",
            metadata: new Dictionary<string, string>
            {
                ["skill_id"] = incident.SkillId,
                ["exit_code"] = incident.ExitCode.ToString(),
            },
            ct);

        Resolution resolution;
        try
        {
            resolution = await _consensus.ResolveAsync(incident, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consensus protocol threw for incident {Id}", incident.IncidentId);
            return;
        }

        await _engram.StoreAsync(
            "lord_helm_incidents",
            incident.IncidentId + "-resolution",
            JsonSerializer.Serialize(resolution),
            category: resolution.EscalatedToHuman ? "escalation" : "resolution",
            metadata: new Dictionary<string, string>
            {
                ["incident_id"] = incident.IncidentId,
                ["unanimous"] = resolution.Unanimous.ToString(),
                ["escalated"] = resolution.EscalatedToHuman.ToString(),
            },
            ct);

        if (resolution.EscalatedToHuman)
        {
            _logger.LogWarning("Incident {Id} escalated to human: {Reason}", incident.IncidentId, resolution.EscalationReason);
        }
        else
        {
            _logger.LogInformation("Incident {Id} resolved: fix={Fix}", incident.IncidentId, resolution.AgreedFix);
        }
    }
}
