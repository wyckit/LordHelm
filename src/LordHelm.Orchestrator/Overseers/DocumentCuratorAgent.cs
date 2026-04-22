using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Overseers;

/// <summary>
/// Worked example of an <see cref="IOverseerAgent"/>. Walks the <c>skills/</c>
/// directory and every <c>src/**/OVERVIEW.md</c> file; reports on:
///   - skills missing a description or tag,
///   - source folders without an OVERVIEW.md,
///   - OVERVIEW.md files that are older than the newest .cs in their folder.
/// When everything looks clean, returns <see cref="OverseerStatus.DoneForNow"/>
/// and pushes a "Documentation current" alert so the tray shows the check passed.
/// </summary>
public sealed class DocumentCuratorAgent : IOverseerAgent
{
    private readonly ILogger<DocumentCuratorAgent> _logger;
    private readonly string _repoRoot;

    public DocumentCuratorAgent(string repoRoot, ILogger<DocumentCuratorAgent> logger)
    {
        _repoRoot = repoRoot;
        _logger = logger;
    }

    public string Id => "document-curator";
    public string Name => "Document Curator";
    public string Description =>
        "Walks skills/ and every src/**/OVERVIEW.md; reports stale or missing documentation. " +
        "Pauses itself once every folder is up to date and re-wakes when the next file change lands.";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(15);

    public async Task<OverseerResult> TickAsync(OverseerContext ctx, CancellationToken ct)
    {
        var findings = new List<string>();

        var skillsDir = Path.Combine(_repoRoot, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var file in Directory.EnumerateFiles(skillsDir, "*.skill.xml", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                var text = await File.ReadAllTextAsync(file, ct);
                if (!text.Contains("<Description>", StringComparison.OrdinalIgnoreCase))
                    findings.Add($"{Short(file)}: missing <Description>");
                if (!text.Contains("<Tags>", StringComparison.OrdinalIgnoreCase))
                    findings.Add($"{Short(file)}: missing <Tags>");
            }
        }

        var srcDir = Path.Combine(_repoRoot, "src");
        if (Directory.Exists(srcDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(srcDir))
            {
                if (ct.IsCancellationRequested) break;
                var overview = Path.Combine(folder, "OVERVIEW.md");
                var csFiles = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                             && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    .ToList();
                if (csFiles.Count == 0) continue;
                if (!File.Exists(overview))
                {
                    findings.Add($"{Short(folder)}: missing OVERVIEW.md");
                    continue;
                }
                var overviewMtime = File.GetLastWriteTimeUtc(overview);
                var newestCs = csFiles.Select(File.GetLastWriteTimeUtc).Max();
                if (newestCs - overviewMtime > TimeSpan.FromDays(14))
                {
                    var ageDays = (int)(newestCs - overviewMtime).TotalDays;
                    findings.Add($"{Short(folder)}: OVERVIEW.md is {ageDays}d older than newest .cs");
                }
            }
        }

        if (findings.Count == 0)
        {
            _logger.LogInformation("DocumentCurator: nothing to curate");
            await ctx.AlertTray.PushAsync(Id, AlertKind.Info,
                "Documentation current",
                "All skill manifests have descriptions + tags and every src/ folder's OVERVIEW.md is within 14 days of its newest .cs file.",
                ct);
            return new OverseerResult(OverseerStatus.DoneForNow,
                "All documentation up to date.");
        }

        var body = string.Join("\n  - ", new[] { "Found " + findings.Count + " item(s):" }.Concat(findings));
        await ctx.AlertTray.PushAsync(Id, AlertKind.Attention,
            $"{findings.Count} doc issue(s)",
            body,
            ct);
        _logger.LogInformation("DocumentCurator: {Count} finding(s)", findings.Count);
        // When we had findings, wake ourselves up sooner on the next pass so the operator
        // sees churn quickly if they're actively editing files.
        return new OverseerResult(OverseerStatus.Working,
            $"{findings.Count} documentation finding(s).",
            NextIntervalOverride: TimeSpan.FromMinutes(5));
    }

    private string Short(string path) =>
        path.StartsWith(_repoRoot, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(_repoRoot.Length).TrimStart('/', '\\')
            : path;
}
