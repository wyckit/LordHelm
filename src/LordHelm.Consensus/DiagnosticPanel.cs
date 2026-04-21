using Microsoft.Extensions.Logging;

namespace LordHelm.Consensus;

public interface IConsensusProtocol
{
    Task<Resolution> ResolveAsync(IncidentNode incident, CancellationToken ct = default);
}

public sealed class DiagnosticPanelOptions
{
    public int MaxRounds { get; set; } = 3;
    public int MinActiveVoters { get; set; } = 2;
}

/// <summary>
/// Unanimous Consensus Protocol implementation. Each round:
/// (1) collect blind simultaneous votes from all panelists;
/// (2) if unanimous YES and fix passes the novelty check -> return as resolved;
/// (3) otherwise extract the minority rationale and feed it into the next round as
///     dissent-propagation context.
/// Hard cap at MaxRounds -> escalate to human.
/// </summary>
public sealed class DiagnosticPanel : IConsensusProtocol
{
    private readonly IReadOnlyList<IPanelVoter> _panel;
    private readonly INoveltyCheck _novelty;
    private readonly DiagnosticPanelOptions _opts;
    private readonly ILogger<DiagnosticPanel> _logger;

    public DiagnosticPanel(IReadOnlyList<IPanelVoter> panel, INoveltyCheck novelty, DiagnosticPanelOptions opts, ILogger<DiagnosticPanel> logger)
    {
        _panel = panel;
        _novelty = novelty;
        _opts = opts;
        _logger = logger;
    }

    public async Task<Resolution> ResolveAsync(IncidentNode incident, CancellationToken ct = default)
    {
        if (_panel.Count < _opts.MinActiveVoters)
        {
            return new Resolution(false, null, Array.Empty<PanelRound>(), true,
                $"Insufficient voters (have {_panel.Count}, need {_opts.MinActiveVoters})");
        }

        var rounds = new List<PanelRound>();
        string? lastDissent = null;

        for (int round = 1; round <= _opts.MaxRounds; round++)
        {
            var voteTasks = _panel.Select(v => v.VoteAsync(incident, lastDissent, ct)).ToArray();
            var votes = await Task.WhenAll(voteTasks);

            var yes = votes.Where(v => v.Approve).ToList();
            var no = votes.Where(v => !v.Approve).ToList();
            var unanimous = no.Count == 0;

            string? deadlock = null;
            if (unanimous)
            {
                var topFix = yes.OrderByDescending(v => v.Confidence).First().ProposedFix;
                if (!await _novelty.IsNovelAsync(topFix, ct))
                {
                    deadlock = "Proposed fix matches a prior escalated failure; not retrying.";
                    rounds.Add(new PanelRound(round, votes, false, deadlock));
                    return new Resolution(false, null, rounds, true, deadlock);
                }
                rounds.Add(new PanelRound(round, votes, true, null));
                return new Resolution(true, topFix, rounds, false, null);
            }

            rounds.Add(new PanelRound(round, votes, false, null));
            lastDissent = string.Join(" | ", no.Select(v => v.Rationale));
            _logger.LogInformation("Consensus round {R}: {Yes}/{Total} yes; propagating minority dissent", round, yes.Count, votes.Length);
        }

        return new Resolution(false, null, rounds, true, $"No unanimity after {_opts.MaxRounds} rounds");
    }
}
