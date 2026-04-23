using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

/// <summary>
/// In-process host handler for the three basic filesystem skills:
/// <c>create-directory</c>, <c>write-file</c>, <c>list-directory</c>.
/// All three Host-tier, gated by the operator approval flow for the
/// mutating ones. Keeps "make me a folder" goals from falling through to
/// an LLM dispatch (which couldn't actually touch the filesystem anyway).
/// </summary>
public sealed class FileSystemHostHandler : IHostSkillHandler
{
    private readonly ILogger<FileSystemHostHandler> _logger;

    public FileSystemHostHandler(ILogger<FileSystemHostHandler> logger) { _logger = logger; }

    public bool Handles(string skillId) => skillId switch
    {
        "create-directory" => true,
        "write-file"       => true,
        "list-directory"   => true,
        _ => false,
    };

    public Task<HostInvocationResult> RunAsync(
        SkillManifest skill, JsonDocument args, ExpertProfile caller, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return Task.FromResult(skill.Id switch
            {
                "create-directory" => CreateDirectory(args, sw),
                "write-file"       => WriteFile(args, sw),
                "list-directory"   => ListDirectory(args, sw),
                _ => new HostInvocationResult(2, "", $"no handler for {skill.Id}", sw.Elapsed),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Skill} failed for {Caller}", skill.Id, caller.ExpertId);
            return Task.FromResult(new HostInvocationResult(
                ExitCode: 1, Stdout: "",
                Stderr: $"{ex.GetType().Name}: {ex.Message}",
                Elapsed: sw.Elapsed));
        }
    }

    private static HostInvocationResult CreateDirectory(JsonDocument args, Stopwatch sw)
    {
        var path = RequireString(args, "path");
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        var msg = existed
            ? $"Directory already existed (no-op): {path}"
            : $"Created directory: {path}";
        return new HostInvocationResult(0, msg, "", sw.Elapsed);
    }

    private static HostInvocationResult WriteFile(JsonDocument args, Stopwatch sw)
    {
        var path = RequireString(args, "path");
        var content = RequireString(args, "content");
        var overwrite = args.RootElement.TryGetProperty("overwrite", out var ov)
            && ov.ValueKind == JsonValueKind.True;
        if (File.Exists(path) && !overwrite)
        {
            return new HostInvocationResult(
                ExitCode: 1, Stdout: "",
                Stderr: $"Refusing to overwrite existing file without overwrite=true: {path}",
                Elapsed: sw.Elapsed);
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Encoding.UTF8);
        var bytes = new FileInfo(path).Length;
        return new HostInvocationResult(0, $"Wrote {bytes} bytes to {path}", "", sw.Elapsed);
    }

    private static HostInvocationResult ListDirectory(JsonDocument args, Stopwatch sw)
    {
        var path = RequireString(args, "path");
        var recursive = args.RootElement.TryGetProperty("recursive", out var r)
            && r.ValueKind == JsonValueKind.True;
        if (!Directory.Exists(path))
            return new HostInvocationResult(1, "", $"Directory not found: {path}", sw.Elapsed);

        var sb = new StringBuilder();
        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        int dirs = 0, files = 0;
        foreach (var d in Directory.EnumerateDirectories(path, "*", search))
        {
            sb.AppendLine($"d  -          {Directory.GetLastWriteTimeUtc(d):yyyy-MM-dd HH:mm}  {d}");
            dirs++;
        }
        foreach (var f in Directory.EnumerateFiles(path, "*", search))
        {
            var fi = new FileInfo(f);
            sb.AppendLine($"f  {fi.Length,10}  {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm}  {f}");
            files++;
        }
        sb.AppendLine().Append($"({dirs} dirs, {files} files)");
        return new HostInvocationResult(0, sb.ToString(), "", sw.Elapsed);
    }

    private static string RequireString(JsonDocument args, string name)
    {
        if (!args.RootElement.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"missing required string argument '{name}'");
        return el.GetString() ?? throw new ArgumentException($"empty string for '{name}'");
    }
}
