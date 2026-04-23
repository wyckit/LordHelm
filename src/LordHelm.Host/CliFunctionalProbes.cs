using System.Diagnostics;
using McpEngramMemory.Core.Services.Evaluation;
using Spectre.Console;

namespace LordHelm.Host;

public sealed record CliProbe(string VendorId, string Executable, IAgentOutcomeModelClient Client, string Model);

public sealed record CliProbeResult(
    string VendorId,
    bool VersionOk,
    string VersionDetail,
    bool GenerateOk,
    string GenerateDetail,
    TimeSpan Elapsed);

/// <summary>
/// Deeper-than-health-check validation that each provider CLI isn't just on PATH,
/// but actually responds to a trivial prompt. Verifies end-to-end that subprocess
/// spawn, stdin piping, output parsing, and model routing all work.
/// Intended to run on demand — not on every Web startup — because each probe
/// costs a real CLI invocation against the subscription.
/// </summary>
public static class CliFunctionalProbes
{
    /// <summary>Default probe set — claude, gemini, codex. Models should match the Program.cs config.</summary>
    public static IReadOnlyList<CliProbe> Defaults() => new[]
    {
        new CliProbe("claude", "claude", new ClaudeCliModelClient(), "claude-opus-4-7"),
        new CliProbe("gemini", "gemini", new GeminiCliModelClient(), "gemini-2.5-pro"),
        new CliProbe("codex",  "codex",  new CodexCliModelClient(),  "gpt-5.4"),
    };

    public static async Task<CliProbeResult> RunAsync(CliProbe probe, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var (versionOk, versionDetail) = await ProbeVersionAsync(probe.Executable, TimeSpan.FromSeconds(5), ct);
        if (!versionOk)
        {
            return new CliProbeResult(probe.VendorId, false, versionDetail, false, "skipped (version probe failed)", sw.Elapsed);
        }

        var (generateOk, generateDetail) = await ProbeGenerateAsync(probe, timeout, ct);
        return new CliProbeResult(probe.VendorId, true, versionDetail, generateOk, generateDetail, sw.Elapsed);
    }

    public static async Task<IReadOnlyList<CliProbeResult>> RunAllAsync(
        IEnumerable<CliProbe> probes,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var results = new List<CliProbeResult>();
        foreach (var p in probes)
        {
            results.Add(await RunAsync(p, timeout, ct));
        }
        return results;
    }

    public static void Render(IReadOnlyList<CliProbeResult> results)
    {
        var table = new Table()
            .AddColumns("Vendor", "Version", "Generate", "Detail", "Elapsed");
        foreach (var r in results)
        {
            var versionCell = r.VersionOk ? "[green]OK[/]" : "[red]FAIL[/]";
            var generateCell = r.GenerateOk ? "[green]OK[/]" : r.VersionOk ? "[red]FAIL[/]" : "[yellow]SKIP[/]";
            var detail = r.GenerateOk ? r.VersionDetail : r.GenerateDetail;
            table.AddRow(r.VendorId, versionCell, generateCell, Markup.Escape(detail), r.Elapsed.ToString(@"ss\.fff") + "s");
        }
        AnsiConsole.Write(table);
    }

    private static async Task<(bool Ok, string Detail)> ProbeVersionAsync(string exe, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return (false, "not on PATH");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0
                ? (true, stdout.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "unknown")
                : (false, $"exit {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Detail)> ProbeGenerateAsync(CliProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        const string prompt =
            "Reply with a single JSON object: {\"ok\":true,\"echo\":\"helm-probe\"}. " +
            "Output the JSON object and nothing else.";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var text = await probe.Client.GenerateAsync(probe.Model, prompt, maxTokens: 64, temperature: 0.0f, cts.Token);
            if (string.IsNullOrWhiteSpace(text)) return (false, "empty response");

            var cleaned = text.Trim();
            var firstBrace = cleaned.IndexOf('{');
            var lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace < 0 || lastBrace <= firstBrace) return (false, "no JSON object: " + Truncate(cleaned, 80));

            var json = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            var echo = doc.RootElement.TryGetProperty("echo", out var echoEl) ? echoEl.GetString() : null;
            if (!ok) return (false, "ok != true");
            if (!string.Equals(echo, "helm-probe", StringComparison.Ordinal)) return (false, "echo != helm-probe");
            return (true, "ok");
        }
        catch (OperationCanceledException)
        {
            return (false, $"timed out after {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
