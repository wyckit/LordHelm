using System.Text.RegularExpressions;
using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Orchestrator.Artifacts;
using LordHelm.Providers.Adapters;

namespace LordHelm.Orchestrator;

/// <summary>
/// Monthly + per-goal ceilings on an Expert's spend. When a ceiling is
/// breached, the <see cref="IExpert.ActAsync"/> call short-circuits with an
/// <c>ExpertActResult</c> that carries <c>BudgetExceeded=true</c>.
/// </summary>
public sealed record ExpertBudget(
    int MaxTokensPerCall = 2048,
    int MaxTokensPerGoal = 20_000,
    decimal MaxUsdPerGoal = 5m);

/// <summary>
/// Declarative policy attached to an Expert: how it prefers to be scheduled
/// (Interactive / Builder / Batch), whether its outputs require operator
/// approval before being acted on, and which router scoring bias to apply.
/// </summary>
public sealed record ExpertPolicy(
    ResourceMode PreferredMode = ResourceMode.Interactive,
    bool RequiresApproval = false,
    string? PinnedVendor = null,
    /// <summary>When true, the safety floor forces any routing through this
    /// persona into a debate panel (min 2 voters) regardless of what the
    /// chat router proposed. Declarative per-expert debate override.</summary>
    bool AlwaysDebate = false,
    /// <summary>Specific model id override. Null = use whatever the adapter
    /// picks for the pinned/preferred vendor. When set, overrides the
    /// persona's default model too.</summary>
    string? PinnedModel = null,
    /// <summary>Extended-thinking / reasoning mode for this expert. Applied
    /// when the pinned/persona model supports it. Off by default to keep
    /// token cost predictable.</summary>
    bool ThinkingEnabled = false);

public sealed record ExpertActRequest(
    string Task,
    int EstimatedContextTokens = 2000,
    bool NeedsToolCalls = false,
    string? ModelOverride = null,
    /// <summary>Approval-gate session id carried through to HostActionRequest
    /// when RequiresApproval fires — lets the originating surface (e.g. a
    /// chat panel's instance) filter pending approvals back to itself.</summary>
    string? SessionId = null);

public sealed record ExpertActResult(
    string ExpertId,
    string VendorUsed,
    string ModelUsed,
    bool Succeeded,
    string Output,
    string? Error,
    bool BudgetExceeded,
    int TokensUsed,
    TimeSpan Elapsed,
    IReadOnlyList<ArtifactEntry>? Artifacts = null);

/// <summary>
/// Identity-bearing "person" in Lord Helm. Wraps an <see cref="ExpertPersona"/>
/// with its own engram namespace (<c>expert_{id}</c>), budget, and policy, and
/// routes its calls through <see cref="IAdapterRouter"/> so the vendor it
/// speaks to is chosen per-task — not hard-coded. Each act is mirrored to the
/// expert's engram namespace so retrospectives and reflection can query it.
/// </summary>
public interface IExpert
{
    string Id { get; }
    string DisplayName { get; }
    string EngramNamespace { get; }
    ExpertPersona Persona { get; }
    ExpertPolicy Policy { get; }
    ExpertBudget Budget { get; }
    int TokensUsedThisGoal { get; }

    Task<ExpertActResult> ActAsync(ExpertActRequest request, CancellationToken ct = default);
    void ResetBudgetWindow();
}

public sealed class Expert : IExpert
{
    private readonly IAdapterRouter _router;
    private readonly IEngramClient? _engram;
    private readonly IApprovalGate? _approvalGate;
    private readonly IArtifactStore? _artifacts;
    private int _tokensUsed;

    public Expert(
        ExpertPersona persona,
        ExpertPolicy policy,
        ExpertBudget budget,
        IAdapterRouter router,
        IEngramClient? engram = null,
        IApprovalGate? approvalGate = null,
        IArtifactStore? artifacts = null)
    {
        Persona = persona;
        Policy = policy;
        Budget = budget;
        _router = router;
        _engram = engram;
        _approvalGate = approvalGate;
        _artifacts = artifacts;
    }

    public string Id => Persona.Id;
    public string DisplayName => Persona.Name;
    public string EngramNamespace => $"expert_{Persona.Id.Replace('-', '_')}";
    public ExpertPersona Persona { get; }
    public ExpertPolicy Policy { get; }
    public ExpertBudget Budget { get; }
    public int TokensUsedThisGoal => _tokensUsed;

    public void ResetBudgetWindow() => Interlocked.Exchange(ref _tokensUsed, 0);

    public async Task<ExpertActResult> ActAsync(ExpertActRequest request, CancellationToken ct = default)
    {
        if (_tokensUsed >= Budget.MaxTokensPerGoal)
            return new ExpertActResult(Id, "", "", false, "", "budget_exceeded_per_goal", true, _tokensUsed, TimeSpan.Zero);

        // Model precedence: per-call override > policy-pinned model > persona default.
        // The /experts page writes into Policy.PinnedModel so the operator's
        // persisted choice beats the persona's compiled-in default.
        var effectiveModel = request.ModelOverride ?? Policy.PinnedModel ?? Persona.Model;
        var routing = new RoutingRequest(
            TaskKind: Persona.PreferredSkills.Count > 0 ? Persona.PreferredSkills[0] : "reasoning",
            EstimatedContextTokens: request.EstimatedContextTokens,
            NeedsToolCalls: request.NeedsToolCalls,
            PreferredMode: Policy.PreferredMode,
            ModelHint: effectiveModel);

        var adapter = _router.Pick(routing, Policy.PinnedVendor ?? Persona.PreferredVendor);
        if (adapter is null)
            return new ExpertActResult(Id, "", "", false, "", "no_adapter_available", false, _tokensUsed, TimeSpan.Zero);

        var tokensCap = Math.Min(Budget.MaxTokensPerCall, Math.Max(256, Budget.MaxTokensPerGoal - _tokensUsed));
        // Thinking mode: if the persona's policy requests extended reasoning
        // AND the chosen model supports it, prepend the portable reasoning
        // preamble (same shape GoalRunner uses for chat-level thinking).
        var prompt = request.Task;
        if (Policy.ThinkingEnabled && SupportsThinking(effectiveModel))
        {
            prompt = "REASONING MODE: Think step by step. Work through your reasoning before the final answer. Show your work.\n\n" + prompt;
        }
        var response = await adapter.GenerateAsync(new AgentRequest(
            Prompt: prompt,
            ModelOverride: effectiveModel,
            MaxTokens: tokensCap,
            Temperature: 0.2f,
            SystemHint: Persona.SystemHint), ct);

        var used = response.Response.Usage.InputTokens + response.Response.Usage.OutputTokens;
        Interlocked.Add(ref _tokensUsed, used);

        // Approval gate: if policy.RequiresApproval=true, the output must clear
        // the operator gate BEFORE we mark the act as successful. The adapter
        // call has already happened (tokens are spent) — the gate controls
        // consumption, not generation. Denials flip Succeeded=false with the
        // gate reason so downstream callers handle it as any other failure.
        if (Policy.RequiresApproval && response.Response.Error is null)
        {
            if (_approvalGate is null)
            {
                return BuildResult(response, used, succeeded: false, errorOverride: "approval_gate_unavailable");
            }

            var preview = response.Response.AssistantMessage;
            if (preview.Length > 800) preview = preview.Substring(0, 800) + "…";

            var decision = await _approvalGate.RequestAsync(new HostActionRequest(
                SkillId: $"expert:{Id}",
                RiskTier: RiskTier.Exec,
                Summary: $"{DisplayName} produced {used}-tok output via {response.VendorId} — approval required before consumption",
                DiffPreview: preview,
                OperatorId: "lord-helm",
                SessionId: request.SessionId ?? "expert-runtime"), ct);

            if (!decision.Approved)
            {
                return BuildResult(response, used, succeeded: false, errorOverride: $"approval_denied: {decision.Reason}");
            }
        }

        if (_engram is not null)
        {
            try
            {
                await _engram.StoreAsync(
                    EngramNamespace,
                    $"act-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    response.Response.AssistantMessage,
                    category: response.Response.Error is null ? "act" : "act-error",
                    metadata: new Dictionary<string, string>
                    {
                        ["vendor"] = response.VendorId,
                        ["model"] = response.ModelId,
                        ["tokens"] = used.ToString(),
                        ["elapsed_ms"] = ((int)response.Elapsed.TotalMilliseconds).ToString(),
                    },
                    ct);
            }
            catch { /* best-effort mirroring */ }
        }

        var artifacts = response.Response.Error is null
            ? await ExtractArtifactsAsync(response.Response.AssistantMessage, ct)
            : null;
        return BuildResult(response, used, succeeded: response.Response.Error is null, artifacts: artifacts);
    }

    private static readonly Regex FencedBlockRegex = new(
        @"```(?<lang>[a-zA-Z0-9_+-]*)\s*\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s)>\]""']+",
        RegexOptions.Compiled);

    /// <summary>
    /// Best-effort artifact extraction from freeform model output. Fenced
    /// code blocks become Code/Diff/Json/Markdown artifacts (kind inferred
    /// from the language tag); raw URLs become Link artifacts. The full
    /// assistant message stays in <see cref="ExpertActResult.Output"/> so
    /// callers that don't consume artifacts still see everything.
    /// </summary>
    private async Task<IReadOnlyList<ArtifactEntry>?> ExtractArtifactsAsync(string output, CancellationToken ct)
    {
        if (_artifacts is null || string.IsNullOrWhiteSpace(output)) return null;
        var list = new List<ArtifactEntry>();

        var fencedRanges = new List<(int, int)>();
        foreach (Match m in FencedBlockRegex.Matches(output))
        {
            fencedRanges.Add((m.Index, m.Index + m.Length));
            var lang = m.Groups["lang"].Value.Trim().ToLowerInvariant();
            var body = m.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body)) continue;
            var (kind, mime) = lang switch
            {
                "json"                 => (ArtifactKind.Json,     "application/json"),
                "diff" or "patch"      => (ArtifactKind.Diff,     "text/x-diff"),
                "md" or "markdown"     => (ArtifactKind.Markdown, "text/markdown"),
                "csv" or "tsv"         => (ArtifactKind.Table,    "text/csv"),
                ""                     => (ArtifactKind.Text,     "text/plain"),
                _                      => (ArtifactKind.Code,     "text/plain"),
            };
            var title = lang.Length == 0 ? "snippet" : $"{lang} block";
            try
            {
                list.Add(await _artifacts.SaveAsync(
                    producedBy: EngramNamespace, scope: Id, kind: kind,
                    title: title, inlineBody: body.TrimEnd(),
                    binary: null, mimeType: mime,
                    language: lang.Length == 0 ? null : lang, ct: ct));
            }
            catch { }
        }

        foreach (Match m in UrlRegex.Matches(output))
        {
            if (fencedRanges.Any(r => m.Index >= r.Item1 && m.Index < r.Item2)) continue;
            var url = m.Value.TrimEnd('.', ',', ';', ')');
            try
            {
                list.Add(await _artifacts.SaveAsync(
                    producedBy: EngramNamespace, scope: Id, kind: ArtifactKind.Link,
                    title: url.Length > 60 ? url.Substring(0, 60) + "…" : url,
                    inlineBody: url, binary: null, mimeType: "text/uri-list", ct: ct));
            }
            catch { }
        }
        return list.Count == 0 ? null : list;
    }

    // Shared heuristic — kept identical to the one in the Throne / Experts
    // page so UI gating and backend wrapping stay in lock-step.
    private static bool SupportsThinking(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        var id = modelId.ToLowerInvariant();
        return id.Contains("opus") || id.Contains("sonnet") ||
               id.Contains("codex-max") || id.Contains("reasoning") ||
               id.Contains("2.5-pro") || id.Contains("3.1-pro") ||
               id.Contains("3-pro") || id.Contains("gpt-5.4") ||
               id.Contains("gpt-5.2") || id.Contains("gpt-5.3");
    }

    private ExpertActResult BuildResult(
        AgentResponse response, int used, bool succeeded,
        string? errorOverride = null,
        IReadOnlyList<ArtifactEntry>? artifacts = null) => new(
            ExpertId: Id,
            VendorUsed: response.VendorId,
            ModelUsed: response.ModelId,
            Succeeded: succeeded,
            Output: response.Response.AssistantMessage,
            Error: errorOverride ?? response.Response.Error?.Message,
            BudgetExceeded: false,
            TokensUsed: used,
            Elapsed: response.Elapsed,
            Artifacts: artifacts);
}
