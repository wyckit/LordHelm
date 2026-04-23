using System.Diagnostics;
using LordHelm.Skills.Transpilation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Scout;

public sealed record ScoutTarget(string VendorId, string Executable, ICliHelpParser Parser);

public sealed class ScoutOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public int StabilityThreshold { get; set; } = 3;
    public IReadOnlyList<ScoutTarget> Targets { get; set; } = Array.Empty<ScoutTarget>();

    /// <summary>Optional Scout-side hydrator that refreshes the JIT transpiler's flag table
    /// on every drift cycle. Wired by the composition root (see LordHelm.Web/Program.cs).</summary>
    public IScoutFlagHydrator? FlagHydrator { get; set; }
}

/// <summary>
/// Cron-lite hosted service: polls each configured CLI's --help + --version on an interval
/// and feeds the parsed CliSpec into the store. Fires MutationEvent callbacks so the
/// transpiler can invalidate its cache.
/// </summary>
public sealed class ScoutService : BackgroundService
{
    private readonly ScoutOptions _options;
    private readonly ICliSpecStore _store;
    private readonly ILogger<ScoutService> _logger;
    private readonly Action<MutationEvent>? _onMutation;

    public ScoutService(ScoutOptions options, ICliSpecStore store, ILogger<ScoutService> logger, Action<MutationEvent>? onMutation = null)
    {
        _options = options;
        _store = store;
        _logger = logger;
        _onMutation = onMutation;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _store.InitializeAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            try { await Task.Delay(_options.Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Targets probed concurrently — three CLIs no longer wait on each
        // other's --help/--version roundtrips. Per-vendor failure stays
        // isolated to its own try/catch so one bad CLI can't kill the sweep.
        var tasks = _options.Targets.Select(t => ProbeTargetAsync(t, ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task ProbeTargetAsync(ScoutTarget t, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            var (help, version) = await ProbeAsync(t.Executable, ct);
            if (help is null || version is null)
            {
                _logger.LogWarning("Scout: {Vendor} unreachable", t.VendorId);
                return;
            }
            var spec = t.Parser.Parse(help, version, DateTimeOffset.UtcNow);
            var mutations = await _store.RecordAsync(spec, _options.StabilityThreshold, ct);

            var flagsChangedForVendor = mutations.Any(m =>
                m.VendorId == t.VendorId &&
                m.Kind is MutationKind.Added or MutationKind.Removed or MutationKind.ChangedType or MutationKind.Archived);
            if (flagsChangedForVendor && _options.FlagHydrator is not null)
            {
                _options.FlagHydrator.DropVendorVersioned(t.VendorId);
                _options.FlagHydrator.Hydrate(t.VendorId, spec.Version,
                    spec.Flags.Select(f => (f.Name, f.Default)));
            }

            foreach (var m in mutations)
            {
                _logger.LogInformation("Scout mutation: {Vendor} {Kind} {Flag}", m.VendorId, m.Kind, m.FlagName);
                _onMutation?.Invoke(m);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scout failed for {Vendor}", t.VendorId);
        }
    }

    private async Task<(string? help, string? version)> ProbeAsync(string executable, CancellationToken ct)
    {
        // --help and --version fire concurrently — no state depends on
        // ordering and each subprocess is independent.
        var helpTask = RunAsync(executable, "--help", ct);
        var versionTask = RunAsync(executable, "--version", ct);
        var help = await helpTask;
        var version = await versionTask;
        return (help, version);
    }

    private async Task<string?> RunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            });
            if (p is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ProbeTimeout);
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
