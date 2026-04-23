using LordHelm.Core;
using LordHelm.Orchestrator.Consult;
using LordHelm.Orchestrator.Cortex;
using LordHelm.Orchestrator.Topology;
using LordHelm.Providers;
using LordHelm.Skills;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record GoalRunRequest(
    string Goal,
    string? PreferredVendor = null,
    string? Model = null,
    int Priority = 0,
    /// <summary>Optional caller-scoped session id (e.g. a chat panel's
    /// instance id). Threaded through to ApprovalGate.RequestAsync so the
    /// originating surface can filter pending approvals back to itself.</summary>
    string? SessionId = null,
    /// <summary>Operator-requested extended-thinking / reasoning mode. When
    /// true, GoalRunner wraps each LLM prompt with a reasoning preamble the
    /// model respects. Ignored when the selected model doesn't support
    /// reasoning (the chat surface already hides the toggle in that case).</summary>
    bool Thinking = false,
    /// <summary>Fallback persona id when the decomposer doesn't set one on a
    /// node. Without this the label ends up tagged with the literal "expert"
    /// which doesn't match any registered IExpert — so the Fleet Roster
    /// never flips anyone to Working even while tasks run. ChatDispatcher
    /// supplies the router's first persona hint here, or a sensible default.</summary>
    string? DefaultPersona = null);

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
    private readonly IExpertRegistry _experts;
    private readonly DataflowTracker? _dataflow;
    private readonly IConsultStrategy? _consult;
    private readonly IPanelRunner? _panel;
    private readonly ILordHelmCortex? _cortex;
    private readonly IGoalProgressSink _sink;
    private readonly ILogger<GoalRunner> _logger;

    public GoalRunner(
        ILordHelmManager manager,
        IExpertProvisioner provisioner,
        ISkillCache skills,
        ISynthesizer synthesizer,
        IProviderOrchestrator providers,
        ExpertDirectory directory,
        IExpertRegistry experts,
        IGoalProgressSink sink,
        ILogger<GoalRunner> logger,
        DataflowTracker? dataflow = null,
        IConsultStrategy? consult = null,
        IPanelRunner? panel = null,
        ILordHelmCortex? cortex = null)
    {
        _manager = manager;
        _provisioner = provisioner;
        _skills = skills;
        _synthesizer = synthesizer;
        _providers = providers;
        _directory = directory;
        _experts = experts;
        _dataflow = dataflow;
        _consult = consult;
        _panel = panel;
        _cortex = cortex;
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
                // Always tag the label with [persona via vendor] — even for
                // solo tasks — so WidgetStateFleetTaskSource.ExtractPersona
                // can attribute the widget back to its expert. Without the
                // tag, solo tasks show up unattributed and the Fleet Roster
                // keeps every expert in Idle even while they're actively
                // running work.
                var personaTag = node.Persona ?? req.DefaultPersona ?? "expert";
                var memberLabel = node.SwarmSize > 1
                    ? $"{node.Goal} [{personaTag} via {vendor} #m{memberIndex}]"
                    : $"{node.Goal} [{personaTag} via {vendor}]";

                await _sink.OnTaskStartedAsync(goalId, memberTaskId, memberLabel, innerCt);
                try
                {
                    // Consult strategy — novelty short-circuit first; otherwise panel-tier
                    // decision. For MVP, Swarm/Debate/Consensus log their tier choice but
                    // execute as Single; a later slice wires them to ConsensusProtocol and
                    // the swarm aggregator.
                    if (_consult is not null && node.SwarmSize <= 1)
                    {
                        var persona = node.Persona is null ? null : _directory.Get(node.Persona);
                        var decision = await _consult.DecideAsync(
                            new ConsultRequest(goalId, node, persona), innerCt);
                        if (decision.Mode == ConsultMode.ShortCircuit && decision.CachedResolution is not null)
                        {
                            await _sink.OnTaskLogAsync(goalId, memberTaskId,
                                $"cortex short-circuit: {decision.Reason}", innerCt);
                            await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, decision.CachedResolution, innerCt);
                            return decision.CachedResolution;
                        }
                        if (decision.Mode is ConsultMode.Swarm or ConsultMode.Debate or ConsultMode.Consensus
                            && _panel is not null)
                        {
                            await _sink.OnTaskLogAsync(goalId, memberTaskId,
                                $"consult tier: {decision.Mode} × {decision.Voters} ({decision.Reason})", innerCt);
                            var panelPrompt = string.IsNullOrWhiteSpace(node.Goal) ? req.Goal : node.Goal;
                            var run = await _panel.RunAsync(decision, panelPrompt, node, innerCt);
                            await _sink.OnTaskLogAsync(goalId, memberTaskId,
                                $"panel finished rounds={run.Rounds} unanimous={run.Unanimous} voters=[{string.Join(',', run.VoterIds)}]", innerCt);
                            // Consensus with non-unanimous outcome = operator decision needed.
                            if (decision.Mode == ConsultMode.Consensus && !run.Unanimous)
                            {
                                var errMsg = $"consensus deadlock after {run.Rounds} rounds — escalating to operator";
                                await _sink.OnTaskLogAsync(goalId, memberTaskId, errMsg, innerCt);
                                await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, errMsg, innerCt);
                                throw new InvalidOperationException(errMsg);
                            }
                            await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, run.Output, innerCt);
                            return run.Output;
                        }
                    }

                    // Route priority:
                    //  1. If the task carries an explicit skill id (decomposer asked for tool use)
                    //     AND the skill exists in the cache, provision an Expert and execute via
                    //     IExecutionRouter. This is the tool-call path.
                    //  2. Otherwise — the much more common case — this is a pure-LLM task:
                    //     call IProviderOrchestrator.GenerateAsync directly with the task goal
                    //     as the prompt, optionally decorated with persona instructions.
                    //     Skipping the transpiler here avoids invoking a CLI with zero args,
                    //     which used to hang waiting for interactive input.
                    // Skill resolution:
                    //  - Prefer whatever the decomposer assigned on the node.
                    //  - If nothing, try keyword-routed PickSkill against the
                    //    loaded skill cache — catches "create a folder" →
                    //    create-directory without needing an LLM round-trip
                    //    for intent classification.
                    var skillId = node.Skill ?? PickSkill(node, skills);
                    if (skillId is not null)
                    {
                        // For filesystem skills we can lift `path` out of the
                        // goal text directly — no LLM round-trip needed to
                        // produce `{"path": "..."}`. For every other skill we
                        // leave ArgsJson as whatever the decomposer set (or
                        // null, which ProvisionRequest defaults to "{}").
                        var argsJson = ExtractArgsForSkill(skillId, node.Goal);
                        var provReq = new ProvisionRequest(
                            ExpertId: $"expert-{memberTaskId}",
                            SkillId: skillId,
                            CliVendorId: vendor,
                            Model: defaultModel,
                            Goal: node.Goal,
                            ArgsJson: argsJson,
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

                    // Pure-LLM path:
                    //  - Single-member task with a named persona → IExpert.ActAsync, which
                    //    applies persona system hint, routes via AdapterRouter on task-kind,
                    //    tracks per-goal token budget, and mirrors the act to the expert's
                    //    own engram namespace.
                    //  - Swarm members (SwarmSize > 1) stay on the provider-orchestrator
                    //    path so each member can pin its rotated vendor explicitly.
                    //  - No persona → orchestrator fallback with the task-kind hint.
                    var runtimeExpert = node.Persona is not null && node.SwarmSize <= 1
                        ? _experts.Get(node.Persona)
                        : null;

                    if (runtimeExpert is not null)
                    {
                        await _sink.OnTaskLogAsync(goalId, memberTaskId,
                            $"acting as {runtimeExpert.Id} (ns={runtimeExpert.EngramNamespace}, mode={runtimeExpert.Policy.PreferredMode})", innerCt);
                        var act = await runtimeExpert.ActAsync(new ExpertActRequest(
                            Task: string.IsNullOrWhiteSpace(node.Goal) ? req.Goal : node.Goal,
                            EstimatedContextTokens: node.EstimatedContextTokens,
                            NeedsToolCalls: node.NeedsToolCalls,
                            ModelOverride: node.ModelHint,
                            SessionId: req.SessionId), innerCt);
                        if (!act.Succeeded)
                        {
                            var errMsg = act.BudgetExceeded
                                ? $"budget exceeded for {runtimeExpert.Id}: {act.Error}"
                                : $"expert error ({act.Error ?? "unknown"})";
                            await _sink.OnTaskLogAsync(goalId, memberTaskId, errMsg, innerCt);
                            await _sink.OnTaskCompletedAsync(goalId, memberTaskId, false, errMsg, innerCt);
                            throw new InvalidOperationException(errMsg);
                        }
                        var output = act.Output ?? string.Empty;
                        await _sink.OnTaskLogAsync(goalId, memberTaskId,
                            $"{runtimeExpert.Id} via {act.VendorUsed} in {act.Elapsed.TotalMilliseconds:0}ms, {act.TokensUsed} tok", innerCt);
                        await _sink.OnTaskLogAsync(goalId, memberTaskId,
                            output.Length > 400 ? output.Substring(0, 400) + "..." : output, innerCt);
                        await _sink.OnTaskCompletedAsync(goalId, memberTaskId, true, output, innerCt);
                        return output;
                    }

                    var prompt = BuildLlmPrompt(node, req.Goal);
                    // Thinking preamble — vendor-agnostic prompt wrapper that
                    // tells any reasoning-capable model to show its work.
                    // Works with every adapter without needing CLI-specific
                    // flags; upstream CLIs ship different --reasoning /
                    // --thinking-budget surfaces and piping native flags
                    // through requires modifying the (frozen) upstream CLI
                    // client. This is the portable equivalent.
                    if (req.Thinking)
                    {
                        prompt = "REASONING MODE: Think step by step. Work through your reasoning before the final answer. Show your work.\n\n" + prompt;
                    }
                    var nodeModel = node.ModelHint ?? defaultModel;
                    await _sink.OnTaskLogAsync(goalId, memberTaskId,
                        $"calling {vendor} ({nodeModel}){(req.Thinking ? " [thinking]" : "")} with {prompt.Length}-char prompt", innerCt);
                    var resp = await _providers.GenerateWithFailoverAsync(
                        preferredVendor: vendor,
                        modelOverride: nodeModel,
                        prompt: prompt,
                        hint: new ProviderTaskHint(
                            TaskKind: node.TaskKind,
                            EstimatedContextTokens: node.EstimatedContextTokens,
                            NeedsToolCalls: node.NeedsToolCalls),
                        maxTokens: 1024,
                        temperature: 0.2f,
                        ct: innerCt);
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

        // Emit dataflow observations for every completed dependency edge so
        // the Topology widget lights up the assembly line with staleness fade.
        if (_dataflow is not null && managerResult.Dag.Count > 0)
        {
            var personaByTask = managerResult.Dag
                .Where(n => n.Persona is not null)
                .ToDictionary(n => n.Id, n => n.Persona!, StringComparer.OrdinalIgnoreCase);
            foreach (var node in managerResult.Dag)
            {
                if (node.Persona is null) continue;
                if (!managerResult.NodeOutputs.ContainsKey(node.Id)) continue;
                foreach (var depId in node.DependsOn)
                {
                    if (!personaByTask.TryGetValue(depId, out var upstream)) continue;
                    if (!managerResult.NodeOutputs.ContainsKey(depId)) continue;
                    _dataflow.Observe(upstream, node.Persona, reason: "dag_edge_completed");
                }
                // Every terminal node feeds synthesiser (the shadow helm node).
                if (!managerResult.Dag.Any(other => other.DependsOn.Contains(node.Id)))
                {
                    _dataflow.Observe(node.Persona, "synthesiser", reason: "dag_leaf_to_synth");
                }
            }
        }

        // Retrospective → cortex_staging (no LLM call — pure structural data).
        if (_cortex is not null)
        {
            try
            {
                await _cortex.StoreRetrospectiveAsync(new CortexRetrospective(
                    GoalId: goalId,
                    Goal: req.Goal,
                    Succeeded: managerResult.Succeeded,
                    DagNodeCount: managerResult.Dag.Count,
                    PersonasInvolved: managerResult.Dag
                        .Where(n => n.Persona is not null)
                        .Select(n => n.Persona!)
                        .Distinct()
                        .ToList(),
                    Synthesis: synthesis,
                    ErrorDetail: managerResult.ErrorDetail,
                    CompletedAt: DateTimeOffset.UtcNow), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "cortex retrospective store failed for goal {Id}", goalId);
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

    // Best-effort arg extraction for skills whose shape is simple enough
    // to parse from natural language. Filesystem skills all take `path` (±
    // a content blob or recursive flag); the LLM decomposer can still
    // override by setting node.ArgsJson explicitly (not implemented yet —
    // for now the decomposer emits tasks by goal text).
    internal static string? ExtractArgsForSkill(string skillId, string goal)
    {
        switch (skillId)
        {
            case "create-directory":
            case "list-directory":
            case "read-file":
                {
                    var p = TryExtractPath(goal);
                    return p is null ? null : System.Text.Json.JsonSerializer.Serialize(new { path = p });
                }
            case "write-file":
                {
                    // Only do the easy case: "write file X with content Y" where
                    // Y is quoted. Complex cases still fall to LLM.
                    var p = TryExtractPath(goal);
                    if (p is null) return null;
                    var content = TryExtractQuoted(goal);
                    return System.Text.Json.JsonSerializer.Serialize(
                        new { path = p, content = content ?? "", overwrite = false });
                }
            default:
                return null;
        }
    }

    // Matches Windows absolute paths (C:\foo\bar), Unix absolute paths
    // (/foo/bar), or any quoted path. First hit wins.
    private static string? TryExtractPath(string goal)
    {
        // Quoted path first — highest precedence, handles "C:\path with spaces"
        var qMatch = System.Text.RegularExpressions.Regex.Match(goal,
            @"[""']([^""']+[\\/][^""']+)[""']");
        if (qMatch.Success) return qMatch.Groups[1].Value;

        // Windows absolute path
        var winMatch = System.Text.RegularExpressions.Regex.Match(goal,
            @"([A-Za-z]:\\[^\s""']+)");
        if (winMatch.Success) return winMatch.Groups[1].Value.TrimEnd('.', ',');

        // Unix absolute path
        var unixMatch = System.Text.RegularExpressions.Regex.Match(goal,
            @"(/[^\s""']+/[^\s""']+)");
        if (unixMatch.Success) return unixMatch.Groups[1].Value.TrimEnd('.', ',');

        return null;
    }

    private static string? TryExtractQuoted(string goal)
    {
        var m = System.Text.RegularExpressions.Regex.Match(goal, @"[""']([^""']{2,})[""']");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? PickSkill(TaskNode node, IReadOnlyList<SkillManifest> skills)
    {
        if (skills.Count == 0) return null;
        var goalLower = node.Goal.ToLowerInvariant();

        // 1. Direct id match — e.g. "run read-file at /tmp/x" → read-file
        foreach (var s in skills)
        {
            if (goalLower.Contains(s.Id.ToLowerInvariant())) return s.Id;
        }

        // 2. Keyword routing for common natural-language shapes. When the
        // operator says "create a folder" we shouldn't need an LLM — route
        // to the host skill directly. Order matters: first match wins, so
        // keep more-specific patterns above more-general ones.
        var keywordRoutes = new (string[] Keywords, string SkillId)[]
        {
            (new[] { "create folder", "create directory", "new folder", "new directory", "make folder", "make directory", "mkdir" }, "create-directory"),
            (new[] { "write file", "save file", "create file", "new file" },                                                         "write-file"),
            (new[] { "list folder", "list directory", "ls ",             "list files" },                                             "list-directory"),
            (new[] { "read file", "read the file", "open file", "cat " },                                                            "read-file"),
            (new[] { "run python", "execute python", "python script" },                                                              "execute-python"),
            (new[] { "run c#", "execute c#", "csharp script" },                                                                      "execute-csharp"),
        };
        var have = new HashSet<string>(skills.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var (keywords, skillId) in keywordRoutes)
        {
            if (!have.Contains(skillId)) continue;
            foreach (var kw in keywords)
            {
                if (goalLower.Contains(kw)) return skillId;
            }
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
