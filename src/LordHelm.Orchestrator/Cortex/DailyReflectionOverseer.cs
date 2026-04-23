using System.Text;
using LordHelm.Core;
using LordHelm.Orchestrator.Overseers;

namespace LordHelm.Orchestrator.Cortex;

/// <summary>
/// Once a day, reads the most recent retrospectives from the cortex, asks
/// the synthesiser expert to distill 3-5 "insight" nodes (cross-cutting
/// patterns, recurring failure modes, unnoticed successes), and stores
/// them back via ThinkAsync so subsequent RecallAcrossFleetAsync calls
/// can retrieve them as context.
///
/// This is the compounding-memory loop: each day of goal runs produces
/// structural retrospectives → this overseer distills → insights become
/// context for future decompositions. The 7-day auto-promote gate still
/// applies, so today's insights don't contaminate recall until they've
/// been reviewed or aged.
/// </summary>
public sealed class DailyReflectionOverseer : IOverseerAgent
{
    private readonly ILordHelmCortex _cortex;
    private readonly IExpertRegistry _experts;

    public DailyReflectionOverseer(ILordHelmCortex cortex, IExpertRegistry experts)
    {
        _cortex = cortex;
        _experts = experts;
    }

    public string Id => "daily-reflection";
    public string Name => "Daily Reflection";
    public string Description =>
        "Reads recent retrospectives from lord_helm_cortex and asks the synthesiser to distill 3-5 cross-cutting insights.";
    public TimeSpan DefaultInterval => TimeSpan.FromHours(24);

    public async Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct)
    {
        var recents = await _cortex.RecallAcrossFleetAsync("recent retrospective", k: 20, ct);
        var retros = recents
            .Where(h => h.Metadata.TryGetValue("category", out var c) && c == "retrospective")
            .OrderByDescending(h =>
                h.Metadata.TryGetValue("completed_at", out var iso) &&
                DateTimeOffset.TryParse(iso, out var parsed)
                    ? parsed : DateTimeOffset.MinValue)
            .Take(20)
            .ToList();
        if (retros.Count < 3)
            return new OverseerResult(OverseerStatus.DoneForNow, "too few retrospectives to reflect over (< 3)");

        var synth = _experts.Get("synthesiser");
        if (synth is null)
            return new OverseerResult(OverseerStatus.Error, "synthesiser expert not registered");

        var sb = new StringBuilder();
        sb.AppendLine("You are Lord Helm reflecting on your fleet's recent work.");
        sb.AppendLine("Read the retrospectives below and distill 3-5 insights.");
        sb.AppendLine("Each insight is 1-2 sentences. Lead with the pattern, then the evidence.");
        sb.AppendLine("Format: numbered list, no preamble, no markdown.");
        sb.AppendLine();
        sb.AppendLine("--- RETROSPECTIVES ---");
        for (int i = 0; i < retros.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {retros[i].Text}");
        }

        var act = await synth.ActAsync(new ExpertActRequest(sb.ToString(), EstimatedContextTokens: 8000), ct);
        if (!act.Succeeded || string.IsNullOrWhiteSpace(act.Output))
            return new OverseerResult(OverseerStatus.Error, $"synthesiser failed: {act.Error}");

        await _cortex.ThinkAsync(
            text: act.Output.Trim(),
            category: "insight",
            metadata: new Dictionary<string, string>
            {
                ["source"] = "daily-reflection",
                ["retros_considered"] = retros.Count.ToString(),
                ["tick"] = ctx.TickNumber.ToString(),
            },
            ct: ct);

        await ctx.AlertTray.PushAsync(
            source: Id, kind: AlertKind.Info,
            title: "Daily reflection stored",
            body: $"Distilled {retros.Count} retrospectives into an insight node.", ct);
        return new OverseerResult(OverseerStatus.Working,
            $"stored insight from {retros.Count} retros");
    }
}
