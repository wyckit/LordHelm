using System.Diagnostics;
using System.Text;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Usage;

/// <summary>
/// Per-vendor auth-probe spec. The probe's only job is "can I make a
/// successful tiny inference call right now?" — exit code 0 + non-empty
/// stdout = AuthOk. Quota/rate-limit strings in stderr flip Exhausted=true.
/// </summary>
public sealed record AuthProbeSpec(
    string VendorId,
    string Executable,
    IReadOnlyList<string> Args,
    string? StdinInput,
    /// <summary>When set, the CLI writes output to this file path instead
    /// of stdout; probe reads + deletes after exit. Codex needs this.</summary>
    bool UsesOutputFile,
    TimeSpan Timeout);

public sealed class CliAuthProbe : IUsageProbe
{
    private readonly AuthProbeSpec _spec;
    private readonly ILogger<CliAuthProbe> _logger;

    public CliAuthProbe(AuthProbeSpec spec, ILogger<CliAuthProbe> logger)
    {
        _spec = spec; _logger = logger;
    }

    public string VendorId => _spec.VendorId;

    public async Task<UsageSnapshot> ProbeAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string? tempOut = null;
        try
        {
            var args = new List<string>(_spec.Args);
            if (_spec.UsesOutputFile)
            {
                tempOut = Path.Combine(Path.GetTempPath(), $"helm-authprobe-{VendorId}-{Guid.NewGuid():N}.txt");
                args.Add("-o");
                args.Add(tempOut);
            }

            // Resolve the bare CLI name via PATHEXT + .cmd shim lookup on
            // Windows so Process.Start isn't handed "codex" (a .cmd shim npm
            // ships) and fails with "An error occurred trying to start
            // process". Mirrors the upstream CliExecutableResolver.Resolve
            // logic used by the real CodexCliModelClient (that type is
            // internal to McpEngramMemory.Core so we replicate inline).
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
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("process failed to start");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_spec.Timeout);

            if (_spec.StdinInput is not null)
            {
                try
                {
                    await p.StandardInput.WriteAsync(_spec.StdinInput.AsMemory(), cts.Token);
                    await p.StandardInput.FlushAsync(cts.Token);
                    p.StandardInput.Close();
                }
                catch { }
            }

            // Drain stdout + stderr CONCURRENTLY. Codex emits progress lines
            // on stderr while it's running; a sequential ReadToEnd on stdout
            // blocks until the process exits, but if stderr's buffer fills
            // up first the process deadlocks waiting on stderr drain. This is
            // exactly how CodexCliModelClient does it for real dispatches.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // If the vendor writes to a temp file, the "real" output lives there.
            var bodyFromFile = "";
            if (tempOut is not null && File.Exists(tempOut))
            {
                try { bodyFromFile = await File.ReadAllTextAsync(tempOut, cts.Token); }
                catch { }
            }
            var body = string.IsNullOrWhiteSpace(bodyFromFile) ? stdout : bodyFromFile;
            var exhausted = LooksExhausted(stderr) || LooksExhausted(stdout);
            var ok = p.ExitCode == 0 && !string.IsNullOrWhiteSpace(body) && !exhausted;

            if (!ok)
            {
                // Dev-server console gets the full story when probe fails so
                // the operator can diff the invocation against a known-good
                // manual run. /diagnostics 'raw' button also exposes this.
                _logger.LogWarning(
                    "auth probe {Vendor} failed: exit={Exit} exhausted={Exhausted} stdout={Stdout} stderr={Stderr}",
                    VendorId, p.ExitCode, exhausted,
                    Truncate(stdout, 400), Truncate(stderr, 400));
            }

            return new UsageSnapshot(
                VendorId:          VendorId,
                RequestsUsed:      null,
                RequestsLimit:     null,
                TokensUsed:        null,
                TokensLimit:       null,
                CostUsd:           null,
                ResetAt:           null,
                AuthOk:            ok,
                Exhausted:         exhausted,
                ResolvedModel:     null,
                RawOutput:         Truncate("STDOUT:\n" + stdout + "\n---STDERR:\n" + stderr +
                                            (bodyFromFile.Length > 0 ? "\n---OUTFILE:\n" + bodyFromFile : ""), 2000),
                Error:             ok ? null : $"exit={p.ExitCode}" + (exhausted ? " (quota)" : ""),
                ProbedAt:          now);
        }
        catch (OperationCanceledException)
        {
            return new UsageSnapshot(VendorId, null, null, null, null, null, null,
                AuthOk: false, Exhausted: false, ResolvedModel: null,
                RawOutput: null, Error: "timeout", ProbedAt: now);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "auth probe {Vendor} threw", VendorId);
            return new UsageSnapshot(VendorId, null, null, null, null, null, null,
                AuthOk: false, Exhausted: false, ResolvedModel: null,
                RawOutput: null, Error: ex.Message, ProbedAt: now);
        }
        finally
        {
            if (tempOut is not null)
            {
                try { File.Delete(tempOut); } catch { }
            }
        }
    }

    // Detect subscription-exhaustion in error text across vendors — heuristic
    // string-match, refined as we see real failures. Deliberately NARROW
    // (only explicit exhaustion phrases) so that routine stderr chatter —
    // progress banners that mention "subscription" or model metadata — doesn't
    // falsely mark a vendor as exhausted and hard-exclude it from the router.
    private static bool LooksExhausted(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var needles = new[]
        {
            "quota exceeded", "quota_exceeded",
            "rate limit exceeded", "rate_limit_exceeded",
            "usage limit reached", "usage_limit_reached",
            "subscription exhausted", "subscription_exhausted",
            "out of credits", "insufficient credits",
            "429 too many requests", "http 429",
            "you have reached your",
        };
        foreach (var n in needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    // Replicates McpEngramMemory.Core's CliExecutableResolver (internal) so
    // the probe resolves npm-installed .cmd shims on Windows exactly like
    // the real dispatch path does.
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

public static class AuthProbeDefaults
{
    /// <summary>
    /// Research-committed auth-probe specs (2026-04-21 panel).
    /// Reference: engram LordHelm/cli-auth-probe-per-vendor-2026-04-21.
    /// </summary>
    /// <summary>
    /// Fallback probe specs used only when <see cref="AuthProbeSpecFactory"/>
    /// can't reach a populated <see cref="IModelCatalog"/> (initial boot
    /// before refresher runs). No --model flag — the CLI picks whatever
    /// the current subscription defaults to. Once the catalog is populated
    /// the factory supersedes these with live Fast-tier model ids.
    /// </summary>
    public static IReadOnlyList<AuthProbeSpec> Defaults() => new[]
    {
        new AuthProbeSpec("claude", "claude", new[] { "-p" },           "Reply with the single word pong.\n", false, TimeSpan.FromSeconds(20)),
        new AuthProbeSpec("gemini", "gemini", new[] { "-p", "" },       "Reply with the single word pong.\n", false, TimeSpan.FromSeconds(20)),
        new AuthProbeSpec("codex",  "codex",  new[] { "exec", "--sandbox", "read-only", "--skip-git-repo-check" },
            "Reply with the single word pong.\n", true,  TimeSpan.FromSeconds(25)),
    };
}

/// <summary>
/// Builds auth-probe specs from the LIVE <see cref="IModelCatalog"/>
/// (Fast-tier resolve per vendor) instead of hardcoded model ids that
/// go stale every vendor release. When the catalog is still empty the
/// fallback shape drops the --model flag and lets the CLI default.
/// </summary>
public sealed class AuthProbeSpecFactory
{
    private readonly IModelCatalog _catalog;

    public AuthProbeSpecFactory(IModelCatalog catalog) { _catalog = catalog; }

    public IReadOnlyList<AuthProbeSpec> Build() =>
        new[] { "claude", "gemini", "codex" }.Select(BuildFor).ToList();

    public AuthProbeSpec BuildFor(string vendor)
    {
        // Prefer the CHEAPEST / most-basic model available for probes —
        // picks the Fast-tier resolve (which prioritises `-mini` / `-flash`
        // variants). Probes don't need reasoning depth; just "can I make a
        // call at all?". This also keeps probe cost at or near zero against
        // the user's subscription budget.
        var probeModel = ResolveProbeModel(vendor);
        return vendor switch
        {
            "claude" => new AuthProbeSpec("claude", "claude",
                probeModel is null ? new[] { "-p" } : new[] { "-p", "--model", probeModel },
                "Reply with the single word pong.\n", false, TimeSpan.FromSeconds(20)),
            "gemini" => new AuthProbeSpec("gemini", "gemini",
                probeModel is null ? new[] { "-p", "" } : new[] { "--model", probeModel, "-p", "" },
                "Reply with the single word pong.\n", false, TimeSpan.FromSeconds(20)),
            "codex" => new AuthProbeSpec("codex", "codex",
                probeModel is null
                    ? new[] { "exec", "--sandbox", "read-only", "--skip-git-repo-check" }
                    : new[] { "exec", "--model", probeModel, "--sandbox", "read-only", "--skip-git-repo-check" },
                "Reply with the single word pong.\n", true, TimeSpan.FromSeconds(25)),
            _ => new AuthProbeSpec(vendor, vendor, Array.Empty<string>(), "Reply with the single word pong.\n", false, TimeSpan.FromSeconds(20)),
        };
    }

    // Per-vendor preferred probe model — the cheapest known-working model
    // the user's subscription reliably grants access to. We deliberately
    // trust these hardcoded names EVEN IF the catalog has them marked
    // unavailable, because the catalog's availability often reflects the
    // LLM-fallback prober's hallucinations (e.g. gemini-1.0-pro) rather
    // than ground truth. The probe is itself the source of truth for
    // availability; passing a known-good name gives the CLI a fair shot.
    private string? ResolveProbeModel(string vendor)
    {
        var preferred = vendor.ToLowerInvariant() switch
        {
            "codex"  => "gpt-5.4-mini",          // "Smaller frontier agentic coding model"
            "claude" => "claude-haiku-4-5",
            "gemini" => "gemini-2.5-flash-lite", // cheapest Fast-tier variant in the user's /model list
            _ => null,
        };
        if (preferred is not null) return preferred;
        // Unknown vendor — let the catalog's Fast tier pick.
        return _catalog.Resolve(ModelTier.Fast, vendor)?.ModelId;
    }
}
