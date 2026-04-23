using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Consult;

public sealed record PanelRunResult(
    string Output,
    IReadOnlyList<string> VoterIds,
    IReadOnlyList<ArtifactEntry> Artifacts,
    int Rounds,
    bool Unanimous);

/// <summary>
/// Executes a <see cref="ConsultDecision"/> of Mode = Swarm / Debate /
/// Consensus. Panel-endorsed shape: fan out N diverse persona picks in
/// parallel, then mode-specific convergence — Swarm aggregates once via
/// the synthesiser; Debate injects minority dissent and re-polls up to
/// 3 rounds; Consensus runs the Debate loop but marks non-unanimous
/// outcomes so the caller can route to ApprovalGate.
/// </summary>
public interface IPanelRunner
{
    Task<PanelRunResult> RunAsync(
        ConsultDecision decision,
        string prompt,
        TaskNode node,
        CancellationToken ct = default);
}

public sealed class PanelRunner : IPanelRunner
{
    private readonly IExpertRegistry _experts;
    private readonly ISwarmAggregator _aggregator;
    private readonly ILogger<PanelRunner> _logger;
    private const int MaxRounds = 3;

    public PanelRunner(IExpertRegistry experts, ISwarmAggregator aggregator, ILogger<PanelRunner> logger)
    {
        _experts = experts;
        _aggregator = aggregator;
        _logger = logger;
    }

    public async Task<PanelRunResult> RunAsync(
        ConsultDecision decision,
        string prompt,
        TaskNode node,
        CancellationToken ct = default)
    {
        var voters = PickDiverseVoters(decision.Voters, node.Persona).ToList();
        if (voters.Count == 0)
        {
            return new PanelRunResult("(no voters available)", Array.Empty<string>(), Array.Empty<ArtifactEntry>(), 0, false);
        }

        var artifacts = new List<ArtifactEntry>();
        var members = new List<SwarmMemberOutput>();
        var rounds = 0;

        // Round 1 — blind parallel vote (every voter gets the same prompt, no cross-visibility).
        rounds++;
        await FanOutOnceAsync(voters, prompt, null, members, artifacts, ct);

        if (decision.Mode == ConsultMode.Swarm)
        {
            var merged = await _aggregator.AggregateAsync(node, members, ct);
            return new PanelRunResult(merged, voters.Select(v => v.Id).ToList(), artifacts, rounds, Unanimous: true);
        }

        // Debate / Consensus — inject minority dissent and re-poll up to MaxRounds.
        while (rounds < MaxRounds)
        {
            var dissent = ExtractDissent(members);
            if (dissent is null) break;  // no meaningful disagreement
            rounds++;
            var newMembers = new List<SwarmMemberOutput>();
            var addendum = "A minority of agents argued: " + dissent + "\nAddress this argument specifically.";
            await FanOutOnceAsync(voters, prompt, addendum, newMembers, artifacts, ct);
            members = newMembers;
        }

        var unanimous = !HasStrongDissent(members);
        var aggregated = await _aggregator.AggregateAsync(node, members, ct);
        return new PanelRunResult(aggregated, voters.Select(v => v.Id).ToList(), artifacts, rounds, unanimous);
    }

    private IEnumerable<IExpert> PickDiverseVoters(int count, string? pinnedPersona)
    {
        if (count <= 0) return Array.Empty<IExpert>();
        var all = _experts.All
            .Where(e => !string.Equals(e.Id, "synthesiser", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0) return Array.Empty<IExpert>();

        var picked = new List<IExpert>();
        if (pinnedPersona is not null)
        {
            var pin = all.FirstOrDefault(e => string.Equals(e.Id, pinnedPersona, StringComparison.OrdinalIgnoreCase));
            if (pin is not null) picked.Add(pin);
        }

        // Greedy loop: on each pick, score every remaining candidate against
        // what's already in `picked`. A candidate is preferred when it
        // introduces a fresh vendor AND a fresh AgentType; ties broken by id
        // so the selection is deterministic. This fixes the earlier bug
        // where OrderBy was evaluated once with `picked` still empty.
        while (picked.Count < count)
        {
            var remaining = all.Where(e => !picked.Any(p => p.Id == e.Id)).ToList();
            if (remaining.Count == 0) break;

            var best = remaining
                .OrderBy(e => picked.Any(p => p.Persona.PreferredVendor == e.Persona.PreferredVendor) ? 1 : 0)
                .ThenBy(e => picked.Any(p => p.Persona.AgentType == e.Persona.AgentType) ? 1 : 0)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .First();
            picked.Add(best);
        }
        return picked;
    }

    private async Task FanOutOnceAsync(
        IEnumerable<IExpert> voters, string prompt, string? addendum,
        List<SwarmMemberOutput> members, List<ArtifactEntry> artifacts, CancellationToken ct)
    {
        var call = addendum is null ? prompt : prompt + "\n\n---\n" + addendum;
        var tasks = voters.Select(async v =>
        {
            try
            {
                var act = await v.ActAsync(new ExpertActRequest(call, EstimatedContextTokens: 4000), ct);
                return (v, act);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "panel voter {Id} threw", v.Id);
                return (v, (ExpertActResult?)null);
            }
        });
        foreach (var (v, act) in await Task.WhenAll(tasks))
        {
            if (act is null || !act.Succeeded || string.IsNullOrWhiteSpace(act.Output)) continue;
            members.Add(new SwarmMemberOutput(
                MemberId: $"{v.Id}-r{members.Count}",
                Persona: v.Id,
                Vendor: act.VendorUsed,
                Output: act.Output));
            if (act.Artifacts is not null) artifacts.AddRange(act.Artifacts);
        }
    }

    // Cheap string-similarity heuristic — if no member diverges from the cluster
    // centroid beyond threshold, there is no dissent to propagate.
    private static string? ExtractDissent(IReadOnlyList<SwarmMemberOutput> members)
    {
        if (members.Count < 2) return null;
        var centroid = members.OrderByDescending(m => m.Output.Length).First().Output;
        SwarmMemberOutput? minority = null;
        double minOverlap = 1.0;
        foreach (var m in members)
        {
            var overlap = TokenOverlap(centroid, m.Output);
            if (overlap < minOverlap) { minOverlap = overlap; minority = m; }
        }
        return minOverlap < 0.55 ? minority?.Output : null;
    }

    private static bool HasStrongDissent(IReadOnlyList<SwarmMemberOutput> members) =>
        members.Count >= 2 && ExtractDissent(members) is not null;

    private static double TokenOverlap(string a, string b)
    {
        var ta = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var tb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var intersect = ta.Intersect(tb, StringComparer.OrdinalIgnoreCase).Count();
        return (double)intersect / Math.Min(ta.Count, tb.Count);
    }
}
