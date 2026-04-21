namespace LordHelm.Monitor;

public enum ProcessEventKind { Started, Stdout, Stderr, Exited, ResourceSample, Incident }

public sealed record ProcessEvent(
    string SubprocessId,
    string Label,
    ProcessEventKind Kind,
    string? Line,
    int? ExitCode,
    double? CpuFraction,
    long? WorkingSetBytes,
    DateTimeOffset At);
