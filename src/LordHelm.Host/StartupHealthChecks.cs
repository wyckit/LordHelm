using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace LordHelm.Host;

public sealed class StartupHealthChecks
{
    private readonly ILogger<StartupHealthChecks> _logger;

    public StartupHealthChecks(ILogger<StartupHealthChecks> logger)
    {
        _logger = logger;
    }

    public async Task<HealthReport> RunAsync(CancellationToken ct)
    {
        var results = new List<HealthResult>
        {
            await CheckDockerAsync(ct),
            await CheckCliAsync("claude", critical: false, ct),
            await CheckCliAsync("gemini", critical: false, ct),
            await CheckCliAsync("codex", critical: false, ct),
        };
        return new HealthReport(results);
    }

    private async Task<HealthResult> CheckDockerAsync(CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "version --format json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return new HealthResult("docker", false, true, "docker CLI not found");
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0
                ? new HealthResult("docker", true, true, "reachable")
                : new HealthResult("docker", false, true, "docker version exited " + p.ExitCode);
        }
        catch (Exception ex)
        {
            return new HealthResult("docker", false, true, ex.Message);
        }
    }

    private async Task<HealthResult> CheckCliAsync(string name, bool critical, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(name, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return new HealthResult(name, false, critical, "not on PATH");
            var completed = await Task.Run(() => p.WaitForExit(5000), ct);
            if (!completed)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new HealthResult(name, false, critical, "timed out");
            }
            return p.ExitCode == 0
                ? new HealthResult(name, true, critical, (await p.StandardOutput.ReadToEndAsync(ct)).Trim())
                : new HealthResult(name, false, critical, $"exit {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return new HealthResult(name, false, critical, ex.Message);
        }
    }
}

public sealed record HealthResult(string Name, bool Ok, bool Critical, string Detail);

public sealed record HealthReport(IReadOnlyList<HealthResult> Results)
{
    public bool AllCritical => Results.Where(r => r.Critical).All(r => r.Ok);

    public void Render()
    {
        var table = new Table().AddColumns("Check", "Status", "Detail");
        foreach (var r in Results)
        {
            var status = r.Ok ? "[green]OK[/]" : r.Critical ? "[red]FAIL[/]" : "[yellow]MISSING[/]";
            table.AddRow(r.Name, status, Markup.Escape(r.Detail));
        }
        AnsiConsole.Write(table);
    }
}
