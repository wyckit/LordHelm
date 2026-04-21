using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Skills;

public sealed record LoadResult(
    int TotalFiles,
    int Loaded,
    int SkippedUnchanged,
    IReadOnlyList<(string File, ValidationReport Report)> Invalid);

public interface ISkillLoader
{
    Task<LoadResult> LoadDirectoryAsync(string skillsDirectory, CancellationToken ct = default);
    Task<SkillManifest?> LoadFileAsync(string filePath, CancellationToken ct = default);
}

public sealed class SkillLoader : ISkillLoader
{
    private readonly ISkillCache _cache;
    private readonly ManifestValidator _validator;
    private readonly ILogger<SkillLoader> _logger;

    public SkillLoader(ISkillCache cache, ManifestValidator validator, ILogger<SkillLoader> logger)
    {
        _cache = cache;
        _validator = validator;
        _logger = logger;
    }

    public async Task<LoadResult> LoadDirectoryAsync(string skillsDirectory, CancellationToken ct = default)
    {
        await _cache.InitializeAsync(ct);

        if (!Directory.Exists(skillsDirectory))
        {
            _logger.LogWarning("Skills directory does not exist: {Dir}", skillsDirectory);
            return new LoadResult(0, 0, 0, Array.Empty<(string, ValidationReport)>());
        }

        var files = Directory.EnumerateFiles(skillsDirectory, "*.skill.xml", SearchOption.AllDirectories).ToList();
        var invalid = new List<(string, ValidationReport)>();
        int loaded = 0, skipped = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var raw = await File.ReadAllTextAsync(file, ct);
                var report = _validator.Validate(raw);
                if (!report.IsValid)
                {
                    invalid.Add((file, report));
                    _logger.LogWarning("Invalid skill {File}: {Errors}", file,
                        string.Join("; ", report.Errors.Select(e => $"[{e.Stage}] {e.Message}")));
                    continue;
                }

                var manifest = SkillManifestParser.Parse(raw);
                if (await _cache.HasHashAsync(manifest.ContentHashSha256, ct))
                {
                    skipped++;
                    continue;
                }
                await _cache.UpsertAsync(file, manifest, ct);
                loaded++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill {File}", file);
                invalid.Add((file, new ValidationReport(new[]
                {
                    new ValidationError(ValidationStage.Xsd, ex.Message, 0, 0)
                })));
            }
        }

        _logger.LogInformation("Skill load: {Loaded} new, {Skipped} unchanged, {Invalid} invalid of {Total}",
            loaded, skipped, invalid.Count, files.Count);
        return new LoadResult(files.Count, loaded, skipped, invalid);
    }

    public async Task<SkillManifest?> LoadFileAsync(string filePath, CancellationToken ct = default)
    {
        var raw = await File.ReadAllTextAsync(filePath, ct);
        var report = _validator.Validate(raw);
        if (!report.IsValid)
        {
            _logger.LogWarning("Invalid skill {File}", filePath);
            return null;
        }
        var manifest = SkillManifestParser.Parse(raw);
        await _cache.UpsertAsync(filePath, manifest, ct);
        return manifest;
    }
}
