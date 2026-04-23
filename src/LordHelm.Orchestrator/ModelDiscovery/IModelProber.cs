using LordHelm.Core;

namespace LordHelm.Orchestrator.ModelDiscovery;

public sealed record ProbedModel(
    string ModelId,
    string Description,
    ModelTier InferredTier);

public sealed record ProbeResult(
    string VendorId,
    bool Succeeded,
    IReadOnlyList<ProbedModel> Models,
    string? Error,
    /// <summary>Truncated raw output the probe captured — stdout+stderr
    /// for CLI probes, the LLM assistant message for fallback probes.
    /// Surfaced on /models/probes so operators can see exactly what the
    /// CLI said when the parser returns zero models. Null when there was
    /// nothing to capture (process failed to start, etc).</summary>
    string? RawOutput = null,
    /// <summary>Which route produced this result — "native-cli" when the
    /// CliModelProber succeeded, "llm-fallback" when the LlmFallbackProber
    /// succeeded, "composite" when used through FallbackCompositeProber.
    /// Null on failure.</summary>
    string? Source = null);

/// <summary>
/// Vendor-specific probe that asks the CLI which models are actually available
/// right now — every CLI exposes this through a slash command (<c>/model</c>) or
/// a subcommand, and the answer drifts with vendor releases. The prober runs
/// one invocation, parses its output, and returns a snapshot that the catalog
/// refresher merges via <see cref="IModelCatalog.Upsert"/>. Failure is soft:
/// on any error the returned <see cref="ProbeResult"/> carries Succeeded=false
/// and the existing catalog entries are preserved.
/// </summary>
public interface IModelProber
{
    string VendorId { get; }
    Task<ProbeResult> ProbeAsync(CancellationToken ct = default);
}
