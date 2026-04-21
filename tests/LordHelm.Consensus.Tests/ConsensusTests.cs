using LordHelm.Consensus;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ConsensusTests
{
    private static readonly IncidentNode Inc = new(
        "i-1", "exec-python", "abc",
        ExitCode: 2, Stdout: "", Stderr: "boom", At: DateTimeOffset.UtcNow);

    private sealed class FixedVoter : IPanelVoter
    {
        public string VoterId { get; }
        public bool Approve { get; set; }
        public string Rationale { get; set; } = "";
        public string Fix { get; set; } = "";
        public FixedVoter(string id) { VoterId = id; }
        public Task<PanelVote> VoteAsync(IncidentNode incident, string? dissent, CancellationToken ct = default) =>
            Task.FromResult(new PanelVote(VoterId, Approve, Rationale, Fix, 0.9));
    }

    [Fact]
    public async Task Unanimous_Yes_Returns_Agreed_Fix()
    {
        var panel = new IPanelVoter[]
        {
            new FixedVoter("a") { Approve = true, Fix = "raise timeout", Rationale = "transient" },
            new FixedVoter("b") { Approve = true, Fix = "raise timeout", Rationale = "agree" },
        };
        var p = new DiagnosticPanel(panel, new TokenOverlapNoveltyCheck(), new DiagnosticPanelOptions(),
            NullLogger<DiagnosticPanel>.Instance);
        var r = await p.ResolveAsync(Inc);
        Assert.True(r.Unanimous);
        Assert.Equal("raise timeout", r.AgreedFix);
        Assert.Single(r.Rounds);
    }

    [Fact]
    public async Task Persistent_Disagreement_Escalates_After_MaxRounds()
    {
        var a = new FixedVoter("a") { Approve = true, Rationale = "go" };
        var b = new FixedVoter("b") { Approve = false, Rationale = "no" };
        var p = new DiagnosticPanel(new IPanelVoter[] { a, b },
            new TokenOverlapNoveltyCheck(),
            new DiagnosticPanelOptions { MaxRounds = 3 },
            NullLogger<DiagnosticPanel>.Instance);
        var r = await p.ResolveAsync(Inc);
        Assert.False(r.Unanimous);
        Assert.True(r.EscalatedToHuman);
        Assert.Equal(3, r.Rounds.Count);
    }

    [Fact]
    public async Task Novelty_Check_Rejects_Duplicate_Fix()
    {
        var novelty = new TokenOverlapNoveltyCheck();
        novelty.RememberPriorFailure("raise timeout");
        var panel = new IPanelVoter[]
        {
            new FixedVoter("a") { Approve = true, Fix = "raise timeout" },
            new FixedVoter("b") { Approve = true, Fix = "raise timeout" },
        };
        var p = new DiagnosticPanel(panel, novelty, new DiagnosticPanelOptions(), NullLogger<DiagnosticPanel>.Instance);
        var r = await p.ResolveAsync(Inc);
        Assert.False(r.Unanimous);
        Assert.True(r.EscalatedToHuman);
        Assert.Contains("prior", r.EscalationReason!, StringComparison.OrdinalIgnoreCase);
    }
}
