using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.ModelDiscovery;

/// <summary>
/// Configuration for a single CLI probe. Different vendors need different
/// invocations: some expose a <c>/model</c> REPL slash command that requires
/// stdin-piping, others accept a flag. The defaults shipped by
/// <see cref="ModelProberDefaults"/> represent best-guess incantations —
/// operators can override per-vendor via <c>appsettings</c> or the
/// <c>/models</c> admin page once a later slice adds that surface.
/// </summary>
public sealed record ModelProbeSpec(
    string VendorId,
    string Executable,
    IReadOnlyList<string> Args,
    string? StdinInput = null,
    TimeSpan? Timeout = null);

public sealed class CliModelProber : IModelProber
{
    private readonly string _vendorId;
    private readonly Func<ModelProbeSpec?> _specResolver;
    private readonly IModelListParser _parser;
    private readonly ILogger<CliModelProber> _logger;

    public CliModelProber(ModelProbeSpec spec, IModelListParser parser, ILogger<CliModelProber> logger)
        : this(spec.VendorId, () => spec, parser, logger) { }

    public CliModelProber(string vendorId, Func<ModelProbeSpec?> specResolver, IModelListParser parser, ILogger<CliModelProber> logger)
    {
        _vendorId = vendorId;
        _specResolver = specResolver;
        _parser = parser;
        _logger = logger;
    }

    public string VendorId => _vendorId;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var _spec = _specResolver() ?? throw new InvalidOperationException($"No probe spec registered for '{_vendorId}'");
        try
        {
            // Resolve npm-installed .cmd shims on Windows (claude/codex/gemini
            // ship as extensionless shims with sibling .cmd files; Process.Start
            // doesn't apply PATHEXT to extensionless names). Same fix the
            // CliAuthProbe + real CodexCliModelClient use.
            var resolved = ResolveWindowsShim(_spec.Executable);
            var psi = new ProcessStartInfo(resolved)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = _spec.StdinInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in _spec.Args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null)
                return new ProbeResult(VendorId, false, Array.Empty<ProbedModel>(), "process_failed_to_start");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_spec.Timeout ?? TimeSpan.FromSeconds(15));

            if (_spec.StdinInput is not null)
            {
                try
                {
                    await p.StandardInput.WriteAsync(_spec.StdinInput.AsMemory(), cts.Token);
                    p.StandardInput.Close();
                }
                catch { /* some CLIs close stdin early */ }
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await p.WaitForExitAsync(cts.Token);

            var raw = stdout + "\n---STDERR---\n" + stderr;
            var rawTruncated = Truncate(raw, 2000);
            var models = _parser.Parse(stdout + "\n" + stderr);

            if (models.Count == 0)
            {
                _logger.LogWarning("Probe {Vendor} returned no parseable models (exit={Exit}). " +
                    "First 200 chars: {Head}", VendorId, p.ExitCode,
                    raw.Length > 200 ? raw.Substring(0, 200) : raw);
                return new ProbeResult(VendorId, false, Array.Empty<ProbedModel>(),
                    $"no_models_parsed (exit={p.ExitCode})",
                    RawOutput: rawTruncated, Source: "native-cli");
            }
            _logger.LogInformation("Probe {Vendor} discovered {N} models", VendorId, models.Count);
            return new ProbeResult(VendorId, true, models, null,
                RawOutput: rawTruncated, Source: "native-cli");
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(VendorId, false, Array.Empty<ProbedModel>(),
                "timeout", RawOutput: null, Source: "native-cli");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Probe {Vendor} threw", VendorId);
            return new ProbeResult(VendorId, false, Array.Empty<ProbedModel>(),
                ex.Message, RawOutput: null, Source: "native-cli");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    // Inline copy of the upstream CliExecutableResolver.Resolve logic — that
    // type is internal to McpEngramMemory.Core so we can't reference it
    // directly. On non-Windows this is a no-op.
    private static string ResolveWindowsShim(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return executable;
        if (!OperatingSystem.IsWindows()) return executable;
        if (Path.HasExtension(executable)) return executable;
        if (Path.IsPathRooted(executable) && File.Exists(executable)) return executable;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return executable;
        string[] extensions = { ".exe", ".cmd", ".bat" };
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), executable + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return executable;
    }
}

public static class ModelProberDefaults
{
    /// <summary>
    /// Best-guess probe specs based on the observed slash-command shape
    /// (<c>/model</c> returns a numbered list). If a vendor's CLI rejects
    /// stdin piping, the call returns an empty parse result and the catalog
    /// falls back to its last-known state or default seed.
    /// </summary>
    public static IReadOnlyList<ModelProbeSpec> Defaults() => new[]
    {
        new ModelProbeSpec("claude", "claude",
            Args: new[] { "/model" },
            StdinInput: null,
            Timeout: TimeSpan.FromSeconds(20)),
        new ModelProbeSpec("gemini", "gemini",
            Args: new[] { "/model" },
            StdinInput: null,
            Timeout: TimeSpan.FromSeconds(20)),
        new ModelProbeSpec("codex",  "codex",
            Args: Array.Empty<string>(),
            StdinInput: "/model\n/exit\n",
            Timeout: TimeSpan.FromSeconds(20)),
    };
}
