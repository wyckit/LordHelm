using LordHelm.Core;

namespace LordHelm.Skills;

public sealed record SkillAuthoringResult(
    bool Succeeded,
    SkillManifest? Manifest,
    string? SavedPath,
    ValidationReport Validation,
    string? ErrorDetail);

/// <summary>
/// Validate-and-save workflow for operator-authored skill manifests. Given raw
/// XML text, runs the full two-stage validator (XSD then JSON Schema Draft 2020-12),
/// canonicalises, hashes, upserts into the SQLite cache, and writes the file to
/// the skills/ directory. The FileSystemWatcher will pick up the file change as
/// well; this path just makes "create through Lord Helm" a first-class action
/// instead of requiring the operator to drop a file by hand.
/// </summary>
public interface ISkillAuthor
{
    Task<SkillAuthoringResult> SaveAsync(string rawXml, bool overwrite = false, CancellationToken ct = default);
}

public sealed class SkillAuthor : ISkillAuthor
{
    private readonly ManifestValidator _validator;
    private readonly ISkillCache _cache;
    private readonly string _skillsDirectory;

    public SkillAuthor(ManifestValidator validator, ISkillCache cache, string skillsDirectory)
    {
        _validator = validator;
        _cache = cache;
        _skillsDirectory = skillsDirectory;
    }

    public async Task<SkillAuthoringResult> SaveAsync(string rawXml, bool overwrite = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawXml))
        {
            return new SkillAuthoringResult(false, null, null, ValidationReport.Valid, "empty XML");
        }

        var report = _validator.Validate(rawXml);
        if (!report.IsValid)
        {
            return new SkillAuthoringResult(false, null, null, report, "validation failed");
        }

        SkillManifest manifest;
        try
        {
            manifest = SkillManifestParser.Parse(rawXml);
        }
        catch (Exception ex)
        {
            return new SkillAuthoringResult(false, null, null, report, "parse error: " + ex.Message);
        }

        Directory.CreateDirectory(_skillsDirectory);
        var filePath = Path.Combine(_skillsDirectory, manifest.Id + ".skill.xml");
        if (File.Exists(filePath) && !overwrite)
        {
            return new SkillAuthoringResult(false, manifest, filePath, report,
                $"a skill named '{manifest.Id}' already exists at {filePath}; re-submit with overwrite=true to replace it");
        }

        await File.WriteAllTextAsync(filePath, rawXml, ct);
        await _cache.UpsertAsync(filePath, manifest, ct);

        return new SkillAuthoringResult(true, manifest, filePath, ValidationReport.Valid, null);
    }
}
