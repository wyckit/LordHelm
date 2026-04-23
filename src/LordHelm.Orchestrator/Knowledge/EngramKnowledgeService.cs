using System.Text;
using System.Text.RegularExpressions;
using LordHelm.Core;
using LordHelm.Providers;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Knowledge;

/// <summary>
/// Engram-first knowledge retrieval. Default recall threshold is intentionally
/// modest so anything seeded into <c>lord_helm_knowledge</c> wins over a
/// research call. Research answers are written back with provenance metadata
/// (vendor, model, researched_at) so later reads know where they came from.
/// </summary>
public sealed class EngramKnowledgeService : IKnowledgeService
{
    public const string Namespace = "lord_helm_knowledge";

    private readonly IEngramClient _engram;
    private readonly IProviderOrchestrator _providers;
    private readonly HelmPreferenceState _preference;
    private readonly ILogger<EngramKnowledgeService> _logger;
    private readonly KnowledgeOptions _options;

    public EngramKnowledgeService(
        IEngramClient engram,
        IProviderOrchestrator providers,
        HelmPreferenceState preference,
        ILogger<EngramKnowledgeService> logger,
        KnowledgeOptions? options = null)
    {
        _engram = engram;
        _providers = providers;
        _preference = preference;
        _logger = logger;
        _options = options ?? new KnowledgeOptions();
    }

    public async Task<KnowledgeResult> RecallOrResearchAsync(string topic, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return new KnowledgeResult("(no topic)", KnowledgeSource.Error,
                null, null, Array.Empty<EngramHit>(), null, "topic was empty");

        IReadOnlyList<EngramHit> hits = Array.Empty<EngramHit>();
        try
        {
            hits = await _engram.SearchAsync(Namespace, topic, _options.TopK, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "engram search failed for topic {Topic}", topic);
        }

        if (hits.Count > 0 && hits[0].Score >= _options.RecallThreshold)
        {
            return new KnowledgeResult(
                Answer: SynthesizeFromHits(topic, hits),
                Source: KnowledgeSource.Engram,
                VendorUsed: null,
                ModelUsed: null,
                Recall: hits,
                StoredNodeId: null,
                Error: null);
        }

        var pref = _preference.Current;
        var prompt = BuildResearchPrompt(topic);
        ProviderResponse response;
        try
        {
            response = await _providers.GenerateWithFailoverAsync(
                preferredVendor: pref.PrimaryVendor,
                modelOverride: pref.PrimaryModel,
                prompt: prompt,
                maxTokens: _options.ResearchMaxTokens,
                temperature: 0.2f,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "research call failed for topic {Topic}", topic);
            return new KnowledgeResult(
                $"Research call threw: {ex.Message}",
                KnowledgeSource.Error, pref.PrimaryVendor, pref.PrimaryModel, hits, null, ex.Message);
        }

        if (response.Error is not null || string.IsNullOrWhiteSpace(response.AssistantMessage))
        {
            var msg = response.Error?.Message ?? "empty research response";
            return new KnowledgeResult(
                $"Research failed: {msg}", KnowledgeSource.Error,
                pref.PrimaryVendor, pref.PrimaryModel, hits, null, msg);
        }

        var nodeId = BuildNodeId(topic);
        try
        {
            await _engram.StoreAsync(
                Namespace, nodeId,
                BuildStoredText(topic, response.AssistantMessage),
                category: "research",
                metadata: new Dictionary<string, string>
                {
                    ["topic"] = topic,
                    ["vendor"] = pref.PrimaryVendor,
                    ["model"] = pref.PrimaryModel ?? "(tier-resolved)",
                    ["tier"] = pref.PrimaryTier,
                    ["researched_at"] = DateTime.UtcNow.ToString("O"),
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to persist knowledge for topic {Topic}", topic);
            nodeId = null;
        }

        return new KnowledgeResult(
            Answer: response.AssistantMessage,
            Source: KnowledgeSource.Research,
            VendorUsed: pref.PrimaryVendor,
            ModelUsed: pref.PrimaryModel,
            Recall: hits,
            StoredNodeId: nodeId,
            Error: null);
    }

    private static string BuildResearchPrompt(string topic) =>
        "You are Lord Helm's research agent. Produce a concise, factual, well-organised " +
        "briefing on the following topic. Use markdown. Flag uncertainty explicitly. " +
        "Aim for 200-500 words with a short summary up top followed by key facts.\n\n" +
        $"Topic: {topic}";

    private static string BuildStoredText(string topic, string body) =>
        $"# {topic}\n\n{body}";

    private static string SynthesizeFromHits(string topic, IReadOnlyList<EngramHit> hits)
    {
        var sb = new StringBuilder();
        sb.Append("Here's what I know about **").Append(topic).AppendLine("** from engram:");
        sb.AppendLine();
        foreach (var h in hits.Take(3))
        {
            sb.Append("— `").Append(h.Id).Append("` (score ")
              .Append(h.Score.ToString("0.00")).AppendLine(")");
            sb.AppendLine(Truncate(h.Text, 600));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string BuildNodeId(string topic)
    {
        var slug = Regex.Replace(topic.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-');
        if (slug.Length == 0) slug = "topic";
        return $"knowledge-{slug}-{DateTime.UtcNow:yyyyMMddHHmm}";
    }
}

public sealed class KnowledgeOptions
{
    public int TopK { get; init; } = 5;
    public double RecallThreshold { get; init; } = 0.55;
    public int ResearchMaxTokens { get; init; } = 800;
}
