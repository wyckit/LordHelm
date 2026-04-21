using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LordHelm.Skills;

/// <summary>
/// FileSystemWatcher with 400ms per-file debounce to collapse editor "save + format" bursts.
/// Failures during watcher setup are logged and swallowed; the process falls back to
/// startup-scan-only mode rather than crashing.
/// </summary>
public sealed class SkillFileWatcher : IDisposable
{
    private readonly ISkillLoader _loader;
    private readonly ILogger<SkillFileWatcher> _logger;
    private readonly FileSystemWatcher? _fsw;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(400);

    public SkillFileWatcher(string directory, ISkillLoader loader, ILogger<SkillFileWatcher> logger)
    {
        _loader = loader;
        _logger = logger;

        try
        {
            _fsw = new FileSystemWatcher(directory, "*.skill.xml")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _fsw.Changed += (_, e) => Debounce(e.FullPath);
            _fsw.Created += (_, e) => Debounce(e.FullPath);
            _fsw.Renamed += (_, e) => Debounce(e.FullPath);
            _fsw.Error += (_, e) => _logger.LogWarning(e.GetException(), "FileSystemWatcher error");
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start FileSystemWatcher on {Dir}; falling back to startup-scan-only.", directory);
            _fsw = null;
        }
    }

    private void Debounce(string path)
    {
        var cts = new CancellationTokenSource();
        var old = _debounce.AddOrUpdate(path, cts, (_, existing) =>
        {
            try { existing.Cancel(); } catch { }
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceWindow, cts.Token);
                await ReloadWithRetryAsync(path);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _debounce.TryRemove(path, out _);
            }
        });
    }

    private async Task ReloadWithRetryAsync(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _loader.LoadFileAsync(path);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reload failed for {Path}", path);
                return;
            }
        }
    }

    public void Dispose() => _fsw?.Dispose();
}
