namespace LordHelm.Core;

/// <summary>
/// Lord Helm's adapter seam: a single, vendor-agnostic contract that a router
/// uses to pick (and hot-swap) the local CLI a given Expert task talks to.
/// The three concrete implementations — <c>ClaudeCodeAdapter</c>,
/// <c>CodexCliAdapter</c>, <c>GeminiCliAdapter</c> — each wrap one subprocess
/// CLI behind this interface so the orchestrator scores + routes on the same
/// shape regardless of vendor.
/// </summary>
public interface IAgentModelAdapter
{
    string VendorId { get; }
    string DefaultModel { get; }
    AdapterCapabilities Capabilities { get; }
    AdapterHealth Health { get; }

    /// <summary>
    /// Effective capabilities at <paramref name="modelId"/>. When the vendor
    /// exposes multiple models with different context/cost/mode, the router
    /// uses this to score each adapter at the model it would actually use.
    /// Returns <see cref="Capabilities"/> when the model is unknown.
    /// </summary>
    AdapterCapabilities ResolveCapabilities(string modelId);

    Task<AgentResponse> GenerateAsync(AgentRequest request, CancellationToken ct = default);
}

/// <summary>
/// Static shape of what an adapter can do. Read by the router at scoring time
/// (capability_match, context_fit, tool_fit, resource_fit) — see
/// <see cref="AdapterRoutingScore"/>. Intended to be cheap to enumerate and
/// stable for the life of the process; dynamic state lives on
/// <see cref="AdapterHealth"/>.
/// </summary>
public sealed record AdapterCapabilities(
    IReadOnlyList<string> SupportedTasks,
    int MaxContextTokens,
    bool SupportsToolCalls,
    bool SupportsStreaming,
    bool SupportsJsonMode,
    ResourceMode Mode,
    CostProfile Cost);

public enum ResourceMode
{
    /// <summary>Chatty, low-latency, single-turn. Good for dashboard-driven UX.</summary>
    Interactive,
    /// <summary>Long-running builder sessions that can churn for minutes per call.</summary>
    Builder,
    /// <summary>Background jobs where throughput + cost dominate latency.</summary>
    Batch
}

/// <summary>
/// Per-million-token price table used by the router's <c>cost_fit</c> weight.
/// Values are illustrative defaults; the real numbers live in
/// <c>data/models.json</c> via the mutable model catalog surface so operators
/// can tune without redeploy.
/// </summary>
public sealed record CostProfile(
    decimal InputPerMTokens,
    decimal OutputPerMTokens,
    decimal CacheReadPerMTokens);

/// <summary>
/// Optional per-model overrides pulled from the catalog. Any field left null
/// falls back to the adapter's baseline <see cref="AdapterCapabilities"/>.
/// Different model ids under the same vendor can therefore carry different
/// context sizes / costs / modes without needing separate adapter classes.
/// </summary>
public sealed record ModelCapabilityOverrides(
    int? MaxContextTokens = null,
    decimal? InputPerMTokens = null,
    decimal? OutputPerMTokens = null,
    bool? SupportsToolCalls = null,
    ResourceMode? Mode = null);

public interface IModelCapabilityProvider
{
    ModelCapabilityOverrides? TryGet(string vendorId, string modelId);
}

/// <summary>
/// Live rolling-window snapshot. Mirrors <c>IProviderHealth.VendorHealth</c> so
/// the summary ribbon and the router can read from the same surface.
/// </summary>
public sealed record AdapterHealth(
    int InFlight,
    int WindowLimit,
    TimeSpan Window,
    bool IsHealthy,
    double RecentSuccessRate,
    string? LastError,
    DateTimeOffset LastProbeUtc,
    decimal RollingCostUsd = 0m,
    int RollingCallCount = 0,
    TimeSpan RollingWindow = default);

/// <summary>
/// Normalised request envelope. An <c>ExpertProfile</c> (identity + memory
/// namespace + skill loadout) builds the prompt and model hint; the router
/// picks the adapter; the adapter translates to the vendor's CLI flags.
/// </summary>
public sealed record AgentRequest(
    string Prompt,
    string? ModelOverride = null,
    int MaxTokens = 512,
    float Temperature = 0.1f,
    string? SystemHint = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Response envelope — canonical shape across vendors. The existing
/// <c>ProviderResponse</c> record is wrapped here so the adapter seam can add
/// timing + vendor echo without rewriting callers.
/// </summary>
public sealed record AgentResponse(
    string VendorId,
    string ModelId,
    ProviderResponse Response,
    TimeSpan Elapsed);
