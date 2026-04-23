using LordHelm.Core;

namespace LordHelm.Skills.Export;

/// <summary>
/// One-way bridge that converts LordHelm <c>.skill.xml</c> manifests into a
/// vendor CLI's native skill/command format and writes them into that CLI's
/// conventional skills directory. Each vendor needs its own exporter because
/// the on-disk shapes are different (Claude Code uses
/// <c>~/.claude/skills/{id}/SKILL.md</c> with YAML frontmatter; OpenAI/Gemini
/// CLIs don't publish a standard skills dir yet).
///
/// Exporters are non-destructive by default: if a target file already exists
/// and <paramref name="overwrite"/> is false, it's skipped so operator-edited
/// skill bodies survive a resync.
/// </summary>
public interface ISkillExporter
{
    /// <summary>Vendor id this exporter targets — "claude" / "codex" / "gemini".</summary>
    string VendorId { get; }

    /// <summary>True when the target CLI is reachable + configured (e.g. the
    /// target directory is writable). UI uses this to enable/disable the
    /// export button per vendor.</summary>
    bool IsSupported();

    /// <summary>Target on-disk location — for display only. Null when
    /// <see cref="IsSupported"/> returns false.</summary>
    string? TargetDirectory { get; }

    Task<ExportReport> ExportAsync(
        IReadOnlyList<SkillManifest> manifests,
        bool overwrite,
        CancellationToken ct = default);
}

public sealed record ExportReport(
    string VendorId,
    int Attempted,
    int Written,
    int Skipped,
    IReadOnlyList<string> Errors,
    string? TargetDirectory);
