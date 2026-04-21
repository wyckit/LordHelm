namespace LordHelm.Core;

public sealed record ExpertProfile(
    string ExpertId,
    string CliVendorId,
    string Model,
    IReadOnlyList<string> SkillLoadout,
    string GoalContext);

public sealed record ToolCall(string Name, string ArgumentsJson);

public sealed record UsageRecord(int InputTokens, int OutputTokens, int CacheReadTokens);

public sealed record ErrorRecord(string Code, string Message);

public sealed record ProviderResponse(
    string AssistantMessage,
    IReadOnlyList<ToolCall> ToolCalls,
    UsageRecord Usage,
    ErrorRecord? Error);

public sealed record SandboxResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed);
