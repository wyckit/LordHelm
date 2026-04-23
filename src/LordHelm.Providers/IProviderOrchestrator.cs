using LordHelm.Core;
using LordHelm.Providers.Adapters;

namespace LordHelm.Providers;

/// <summary>
/// Hint carried through the LLM-call pipeline (decomposer → aggregator →
/// synthesiser) so the router can score the real task kind instead of the
/// placeholder "code" default. Adapter-free callers that don't care can
/// leave this null and the router will match generically.
/// </summary>
public sealed record ProviderTaskHint(
    string? TaskKind,
    int EstimatedContextTokens = 2000,
    bool NeedsToolCalls = false,
    ResourceMode PreferredMode = ResourceMode.Interactive);

/// <summary>
/// Stable provider-facing contract. Implemented today by
/// <see cref="Adapters.AdapterProviderOrchestrator"/>, which composes the
/// adapter registry and router to satisfy existing callers
/// (GoalRunner, LlmSwarmAggregator, LlmGoalDecomposer, LlmSynthesizer, /providers).
/// </summary>
public interface IProviderOrchestrator
{
    IReadOnlyList<string> VendorIds { get; }

    Task<ProviderResponse> GenerateAsync(
        string vendorId,
        string? modelOverride,
        string prompt,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default);

    Task<ProviderResponse> GenerateWithFailoverAsync(
        string preferredVendor,
        string? modelOverride,
        string prompt,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default);

    Task<ProviderResponse> GenerateWithFailoverAsync(
        string preferredVendor,
        string? modelOverride,
        string prompt,
        ProviderTaskHint hint,
        int maxTokens = 512,
        float temperature = 0.1f,
        CancellationToken ct = default);
}
