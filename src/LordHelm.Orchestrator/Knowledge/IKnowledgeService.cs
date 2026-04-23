using LordHelm.Core;

namespace LordHelm.Orchestrator.Knowledge;

/// <summary>
/// "What do you know about X?" — recalls from engram first, falls back to
/// CLI-driven research when recall is weak, then persists the result so
/// subsequent queries hit engram instead of re-researching.
/// </summary>
public interface IKnowledgeService
{
    Task<KnowledgeResult> RecallOrResearchAsync(
        string topic,
        CancellationToken ct = default);
}

public enum KnowledgeSource { Engram, Research, Error }

public sealed record KnowledgeResult(
    string Answer,
    KnowledgeSource Source,
    string? VendorUsed,
    string? ModelUsed,
    IReadOnlyList<EngramHit> Recall,
    string? StoredNodeId,
    string? Error);
