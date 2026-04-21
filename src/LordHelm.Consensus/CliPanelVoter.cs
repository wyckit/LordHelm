using System.Text.Json;
using McpEngramMemory.Core.Services.Evaluation;
using Microsoft.Extensions.Logging;

namespace LordHelm.Consensus;

/// <summary>
/// Wraps any <see cref="IAgentOutcomeModelClient"/> (e.g. ClaudeCliModelClient,
/// GeminiCliModelClient, CodexCliModelClient) as a blind panel voter. Voting
/// happens via a JSON-constrained prompt that asks for approve/deny + rationale +
/// a proposed fix. Parse failures fall back to a conservative abstain-equivalent
/// (approve=false, low confidence).
/// </summary>
public sealed class CliPanelVoter : IPanelVoter
{
    private readonly IAgentOutcomeModelClient _client;
    private readonly string _model;
    private readonly ILogger<CliPanelVoter>? _logger;

    public string VoterId { get; }

    public CliPanelVoter(string voterId, IAgentOutcomeModelClient client, string model, ILogger<CliPanelVoter>? logger = null)
    {
        VoterId = voterId;
        _client = client;
        _model = model;
        _logger = logger;
    }

    public async Task<PanelVote> VoteAsync(IncidentNode incident, string? dissentingRationale, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(incident, dissentingRationale);
        string? raw = null;
        try
        {
            raw = await _client.GenerateAsync(_model, prompt, maxTokens: 320, temperature: 0.1f, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voter {Voter} failed to generate a vote", VoterId);
        }

        return ParseVote(VoterId, raw);
    }

    internal static string BuildPrompt(IncidentNode i, string? dissent)
    {
        var dissentBlock = dissent is null
            ? ""
            : $"\n\nA minority of agents argued: {dissent}\nAddress this argument specifically in your response.";

        return $@"You are a diagnostic panelist reviewing a sandbox execution failure.

INCIDENT
  skill: {i.SkillId}
  exit:  {i.ExitCode}
  args:  {i.ArgsHash}
STDOUT (truncated):
{Truncate(i.Stdout, 800)}
STDERR (truncated):
{Truncate(i.Stderr, 800)}{dissentBlock}

Respond with a JSON object with these keys exactly:
{{ ""approve"": true|false, ""rationale"": ""short reason"", ""fix"": ""one-line proposed fix"", ""confidence"": 0.0-1.0 }}
Only output the JSON object. No surrounding prose.";
    }

    internal static PanelVote ParseVote(string voterId, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new PanelVote(voterId, false, "no response", "", 0.0);

        var trimmed = raw.Trim();
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            return new PanelVote(voterId, false, $"non-json: {Truncate(trimmed, 80)}", "", 0.0);

        var json = trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var approve = root.TryGetProperty("approve", out var a) && a.ValueKind == JsonValueKind.True;
            var rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
            var fix = root.TryGetProperty("fix", out var f) ? f.GetString() ?? "" : "";
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5;
            return new PanelVote(voterId, approve, rationale, fix, Math.Clamp(confidence, 0.0, 1.0));
        }
        catch (JsonException)
        {
            return new PanelVote(voterId, false, $"parse error: {Truncate(json, 80)}", "", 0.0);
        }
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");
}
