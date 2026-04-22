using LordHelm.Core;
using LordHelm.Providers;
using LordHelm.Skills;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record GoalRunRequest(
    string Goal,
    string? PreferredVendor = null,
    string? Model = null,
    int Priority = 0);

public sealed record GoalRunResult(
    string GoalId,
    bool Succeeded,
    int DagNodeCount,
    IReadOnlyDictionary<string, string> NodeOutputs,
    string? Synthesis,
    string? ErrorDetail);

/// <summary>
/// End-to-end goal execution. Combines Manager + Provisioner + Synthesiser into a
/// single call so every caller (HTTP, UI, CLI, MCP) goes through the same path.
/// Emits progress to the registered <see cref="IGoalProgressSink"/>.
/// </summary>
public interface IGoalRunner
{
    Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default);
}

public sealed class GoalRunner : IGoalRunner
{
    private static readonly string[] _vendorRotation = { "claude", "gemini", "codex" };

    private readonly ILordHelmManager _manager;
    private readonly IExpertProvisioner _provisioner;
    private readonly ISkillCache _skills;
    private readonly ISynthesizer _synthesizer;
    private readonly IProviderOrchestrator _providers;
    private readonly ExpertDirectory _directory;
    private readonly IGoalProgressSink _sink;
    private readonly ILogger<GoalRunner> _logger;

    public GoalRunner(
        ILordHelmManager manager,
        IExpertProvisioner provisioner,
        ISkillCache skills,
        ISynthesizer synthesizer,
        IProviderOrchestrator providers,
        ExpertDirectory directory,
        IGoalProgressSink sink,
        ILogger<GoalRunner> logger)
    {
        _manager = manager;
        _provisioner = provisioner;
        _skills = skills;
        _synthesizer = synthesizer;
        _providers = providers;
        _directory = directory;
        _sink = sink;
        _logger = logger;
    }

    public async Task<GoalRunResult> RunAsync(GoalRunRequest req, CancellationToken ct = default)
    {
        var goalId = "goal-" + Guid.NewGuid().ToString("N")[..10];
        var defaultVendor = req.PreferredVendor ?? "claude";
        var defaultModel = req.Model ?? "claude-opus-4-7";

        var skills = await _skills.ListAsync(ct);
        _logger.LogInformation("Goal {Id} starting: {Goal}", goalId, req.Goal);

        await _sink.OnGoalStartedAsync(goalId, req.Goal, 0, ct);

        var managerResult = await _manager.RunAsync(
            req.Goal,
            skills,
            executeTask: async (node, memberIndex, innerCt) =>
            {
                var vendor = node.PreferredVendor
                    ?? (node.SwarmSize > 1 && node.SwarmStrategy == SwarmStrategy.Diverse
                        ? _vendorRotation[memberIndex % _vendorRotation.Length]
                        : defaultVendor);
                var memberTaskId = node.SwarmSize > 1 ? $"{node.Id}#m{memberIndex}" : node.Id;
                var memberLabel = node.SwarmSize > 1
                    ? $"{node.Goal} [{node.Persona ?? "expert"} via {vendor} #m{memberIndex}]"
                    : node.Goal;

                await _sink.OnTaskStartedAsync(goalId, memberTaskId, memberLabel, innerCt);
                try
                {
                    // Route priority:
                    //  1. If the task carries an explicit skill id (decomposer asked for tool use)
                    //     AND the skill exists in the cache, provision an Expert and execute via
                    //     IExecutionRouter. This is the tool-call path.
                    //  2. Otherwise — the much more common case — this is a pure-LLM task:
                    //     call IProviderOrchestrator.GenerateAsync directly with the task goal
                    //     as the prompt, optionally decorated with persona instructions.
                    //     Skipping the transpiler here avoids invoking a CLI with zero args,
                    //     which used to hang waiting for interactive input.
                    var skillId = node.Skill;
                    if (skillId is not null)
                    {
                        var provReq = new ProvisionRequest(
                            ExpertId: $"expert-{memberTaskId}",
                            SkillId: skillId,
                            CliVendorId: vendor,
                            Model: defaultModel,
                            Goal: node.Goal,
                            Persona: node.Persona);
                        var expert = await _provisioner.ProvisionAsync(provReq, innerCt);
                        if (expert is null)
                        {
                            // Skill not loaded — fall through to LLM path rather than fail hard.
                            await _sink.OnTaskLogAsync(goalId, memberTaskId,
                                $"skill '{skillId}' not in cache; falling back to direct LLM call", innerCt);
                        }
                        else
                        {
                            await _sink.OnTaskLogAsync(goalId, memberTaskId,
                                $"provisioned {expert.Profile.ExpertId} with skill '{skillId}' on {vendor}", innerCt);
                            var toolOutput = await expert.Run(innerCt);
                            await _sink.OnTaskLogAsync(goalId, memberTaskId, toolOutput, innerCt);
                            await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, toolOutput, innerCt);
                            return toolOutput;
                        }
                    }

                    // Pure-LLM path: build the prompt and call the provider orchestrator.
                    var prompt = BuildLlmPrompt(node, req.Goal);
                    await _sink.OnTaskLogAsync(goalId, memberTaskId,
                        $"calling {vendor} ({defaultModel}) with {prompt.Length}-char prompt", innerCt);
                    var resp = await _providers.GenerateWithFailoverAsync(
                        preferredVendor: vendor,
                        modelOverride: defaultModel,
                        prompt: prompt,
                        maxTokens: 1024,
                        temperature: 0.2f,
                        innerCt);
                    if (resp.Error is not null)
                    {
                        var errMsg = $"provider error ({resp.Error.Code}): {resp.Error.Message}";
                        await _sink.OnTaskLogAsync(goalId, memberTaskId, errMsg, innerCt);
                        await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, errMsg, innerCt);
                        throw new InvalidOperationException(errMsg);
                    }
                    var llmOutput = resp.AssistantMessage ?? string.Empty;
                    await _sink.OnTaskLogAsync(goalId, memberTaskId,
                        llmOutput.Length > 400 ? llmOutput.Substring(0, 400) + "..." : llmOutput, innerCt);
                    await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, llmOutput, innerCt);
                    return llmOutput;
                }
                catch (Exception ex)
                {
                    await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, ex.Message, innerCt);
                    throw;
                }
            },
            ct);

        string? synthesis = null;
        if (managerResult.Succeeded)
        {
            try
            {
                synthesis = await _synthesizer.SynthesizeAsync(
                    new SynthesisRequest(req.Goal, managerResult.Dag, managerResult.NodeOutputs),
                    ct);
                await _sink.OnTaskLogAsync(goalId, "goal", "synthesis: " + Truncate(synthesis, 200), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Synthesis failed for goal {Id}", goalId);
            }
        }

        await _sink.OnGoalCompletedAsync(goalId, managerResult.Succeeded,
            managerResult.ErrorDetail ?? synthesis, ct);

        return new GoalRunResult(
            goalId,
            managerResult.Succeeded,
            managerResult.Dag.Count,
            managerResult.NodeOutputs,
            synthesis,
            managerResult.ErrorDetail);
    }

    public static string? PickSkill(TaskNode node, IReadOnlyList<SkillManifest> skills)
    {
        if (skills.Count == 0) return null;
        var goalLower = node.Goal.ToLowerInvariant();
        foreach (var s in skills)
        {
            if (goalLower.Contains(s.Id.ToLowerInvariant())) return s.Id;
        }
        return null;
    }

    /// <summary>
    /// Build the prompt that a pure-LLM task node should send to its chosen vendor.
    /// When the task has a named persona, the persona's SystemHint prefixes the
    /// prompt so the model responds in character. Otherwise the raw goal is used.
    /// </summary>
    internal string BuildLlmPrompt(TaskNode node, string originalGoal)
    {
        if (node.Persona is not null)
        {
            var persona = _directory.Get(node.Persona);
            if (persona is not null)
            {
                return persona.SpawnInstructions(node.Goal);
            }
        }
        // Fall back to the task goal — or the top-level user goal if the decomposer
        // produced only a single trivial root node (common in passthrough mode).
        return string.IsNullOrWhiteSpace(node.Goal) ? originalGoal : node.Goal;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
