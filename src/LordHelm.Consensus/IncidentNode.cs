namespace LordHelm.Consensus;

public sealed record IncidentNode(
    string IncidentId,
    string SkillId,
    string ArgsHash,
    int ExitCode,
    string Stdout,
    string Stderr,
    DateTimeOffset At);

public sealed record PanelVote(
    string VoterId,
    bool Approve,
    string Rationale,
    string ProposedFix,
    double Confidence);

public sealed record PanelRound(
    int RoundNumber,
    IReadOnlyList<PanelVote> Votes,
    bool Unanimous,
    string? Deadlock);

public sealed record Resolution(
    bool Unanimous,
    string? AgreedFix,
    IReadOnlyList<PanelRound> Rounds,
    bool EscalatedToHuman,
    string? EscalationReason);
