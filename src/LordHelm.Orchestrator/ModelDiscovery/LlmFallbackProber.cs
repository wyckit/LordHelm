using LordHelm.Providers;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.ModelDiscovery;

/// <summary>
/// Vendor → preferred probe model. Picks the cheapest "always-available"
/// model the user's subscription grants on each vendor so the fallback
/// probe doesn't burn flagship tokens (and doesn't fail on subscription
/// tiers that don't grant flagship access — like ChatGPT subscriptions
/// without API keys).
/// </summary>
internal static class FallbackProbeModels
{
    public static string? PreferredFor(string vendorId) => vendorId.ToLowerInvariant() switch
    {
        "claude" => "claude-haiku-4-5",
        "gemini" => "gemini-2.5-flash-lite",
        "codex"  => "gpt-5.4-mini",
        _ => null,
    };
}

/// <summary>
/// Backup prober that asks the vendor's own LLM to list the models it knows
/// about, when the native <c>/model</c> pipe returns nothing. The prompt is
/// deliberately strict about output format so the same
/// <see cref="NumberedListModelParser"/> that handles CLI output handles this
/// too. This is a best-effort discovery channel — the answer reflects the
/// model's training data, so operators should still verify via the real CLI
/// once available.
/// </summary>
public sealed class LlmFallbackProber : IModelProber
{
    private readonly string _vendorId;
    private readonly IProviderOrchestrator _providers;
    private readonly IModelListParser _parser;
    private readonly ILogger<LlmFallbackProber> _logger;

    public LlmFallbackProber(
        string vendorId,
        IProviderOrchestrator providers,
        IModelListParser parser,
        ILogger<LlmFallbackProber> logger)
    {
        _vendorId = vendorId;
        _providers = providers;
        _parser = parser;
        _logger = logger;
    }

    public string VendorId => _vendorId;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        // Anchor on a self-introspection prompt — the model lists ITS OWN
        // siblings (which it actually knows about because the CLI session
        // exposes the current `/model` catalogue). Prevents hallucinating
        // models from the model's training data.
        var prompt =
            $"You are running inside the {_vendorId} CLI right now. " +
            $"List every model id this CLI's `/model` command currently exposes. " +
            "Output ONLY a numbered list, one model per line, in this exact shape:\n" +
            "1. <model-id>  <one-line description>\n" +
            "2. <model-id>  <one-line description>\n" +
            "Use the canonical model ids as the CLI prints them — no markdown fences, " +
            "no backticks, no preamble. If you do not know, output 'none'.";

        // Use the cheapest known-safe probe model on this vendor, not the
        // flagship default. Same model the auth probe uses — keeps probe
        // cost flat and avoids subscription-tier access errors on flagships.
        var probeModel = FallbackProbeModels.PreferredFor(_vendorId);

        try
        {
            var response = await _providers.GenerateAsync(
                vendorId: _vendorId,
                modelOverride: probeModel,
                prompt: prompt,
                maxTokens: 512,
                temperature: 0.0f,
                ct: ct);
            if (response.Error is not null)
            {
                _logger.LogInformation("LLM fallback probe {Vendor} returned error: {Err}",
                    _vendorId, response.Error.Message);
                return new ProbeResult(_vendorId, false, Array.Empty<ProbedModel>(),
                    response.Error.Message,
                    RawOutput: Truncate(response.AssistantMessage ?? "", 2000),
                    Source: "llm-fallback");
            }
            var rawText = response.AssistantMessage ?? "";
            var rawTruncated = Truncate(rawText, 2000);
            var models = _parser.Parse(rawText);
            if (models.Count == 0)
            {
                return new ProbeResult(_vendorId, false, Array.Empty<ProbedModel>(),
                    "no_models_parsed",
                    RawOutput: rawTruncated, Source: "llm-fallback");
            }
            _logger.LogInformation("LLM fallback probe {Vendor} discovered {N} models", _vendorId, models.Count);
            return new ProbeResult(_vendorId, true, models, null,
                RawOutput: rawTruncated, Source: "llm-fallback");
        }
        catch (Exception ex)
        {
            return new ProbeResult(_vendorId, false, Array.Empty<ProbedModel>(),
                ex.Message, RawOutput: null, Source: "llm-fallback");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}

/// <summary>
/// Composes a native <see cref="CliModelProber"/> with an
/// <see cref="LlmFallbackProber"/>: if the native probe fails, the fallback
/// fires automatically. The composed prober reports success if either path
/// returned a non-empty result.
/// </summary>
public sealed class FallbackCompositeProber : IModelProber
{
    private readonly IModelProber _primary;
    private readonly IModelProber _fallback;
    private readonly ILogger<FallbackCompositeProber> _logger;

    public FallbackCompositeProber(IModelProber primary, IModelProber fallback, ILogger<FallbackCompositeProber> logger)
    {
        if (!string.Equals(primary.VendorId, fallback.VendorId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Primary and fallback probers must target the same vendor");
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public string VendorId => _primary.VendorId;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var first = await _primary.ProbeAsync(ct);
        if (first.Succeeded) return first;
        _logger.LogInformation("Primary probe {Vendor} failed ({Err}); trying LLM fallback.",
            VendorId, first.Error ?? "unknown");
        var second = await _fallback.ProbeAsync(ct);
        // Composite returns the second attempt's body but preserves the
        // primary's raw output inline so operators can see BOTH routes.
        if (second.Succeeded)
        {
            return second with
            {
                RawOutput = $"[native-cli attempt]\n{first.RawOutput ?? "(no output)"}\n\n" +
                            $"[llm-fallback attempt]\n{second.RawOutput ?? "(no output)"}",
                Source = second.Source ?? "llm-fallback"
            };
        }
        // Both failed — surface the fallback's error + both raw outputs.
        return second with
        {
            RawOutput = $"[native-cli attempt]\n{first.RawOutput ?? "(no output)"}\n\n" +
                        $"[llm-fallback attempt]\n{second.RawOutput ?? "(no output)"}",
            Error = $"primary: {first.Error ?? "unknown"} · fallback: {second.Error ?? "unknown"}"
        };
    }
}
