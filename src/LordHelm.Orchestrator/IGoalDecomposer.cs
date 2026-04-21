using LordHelm.Core;

namespace LordHelm.Orchestrator;

public interface IGoalDecomposer
{
    Task<IReadOnlyList<TaskNode>> DecomposeAsync(string goal, IReadOnlyList<SkillManifest> availableSkills, CancellationToken ct = default);
}

/// <summary>
/// Trivial decomposer: emits a single-node DAG ("leaf") per goal. Used as a fallback
/// when an LLM-based decomposer is unavailable (dev mode, degraded operation).
/// </summary>
public sealed class PassthroughGoalDecomposer : IGoalDecomposer
{
    public Task<IReadOnlyList<TaskNode>> DecomposeAsync(string goal, IReadOnlyList<SkillManifest> availableSkills, CancellationToken ct = default)
    {
        IReadOnlyList<TaskNode> nodes = new[] { new TaskNode("root", goal, Array.Empty<string>()) };
        return Task.FromResult(nodes);
    }
}
