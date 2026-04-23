using LordHelm.Core;

namespace LordHelm.Orchestrator.Usage;

/// <summary>
/// RESEARCH-FINDING (2026-04-21): none of the three subscription CLIs
/// (claude/gemini/codex) expose a /status, /usage, or /cost endpoint.
/// <c>--version</c> only proves binary presence, not auth. The only reliable
/// way to verify a vendor is usable is to run the CHEAPEST possible
/// inference call against its CHEAPEST model — exactly what this interface
/// does.
///
/// Claude:  echo "ok" | claude -p --model claude-haiku-4-5
/// Gemini:  echo "ok" | gemini --model gemini-2.5-flash-lite -p ""
/// Codex:   echo "ok" | codex exec --model gpt-5.4-mini --sandbox read-only
///                                 --skip-git-repo-check -o &lt;tmpfile&gt;
///
/// The probe result populates <see cref="UsageState"/> with
/// auth status + resolved model + error signature. Cumulative usage
/// numbers accumulate separately via <see cref="UsageAccumulator"/> from
/// every real adapter call's <see cref="UsageRecord"/>.
/// </summary>
public interface IUsageProbe
{
    string VendorId { get; }
    Task<UsageSnapshot> ProbeAsync(CancellationToken ct = default);
}
