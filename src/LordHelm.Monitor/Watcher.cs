using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using CliWrap;
using CliWrap.EventStream;
using Microsoft.Extensions.Logging;

namespace LordHelm.Monitor;

public sealed record LaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Label,
    string SubprocessId,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record ProcessHandle(string SubprocessId, Task<int> ExitTask, LogRing Logs);

public interface IProcessMonitor
{
    ChannelReader<ProcessEvent> Events { get; }
    ProcessHandle Launch(LaunchSpec spec, CancellationToken ct = default);
    IReadOnlyDictionary<string, LogRing> Logs { get; }
}

/// <summary>
/// CliWrap-based process watcher: launches subprocesses, streams stdout/stderr lines
/// to a channel and a per-subprocess ring buffer, samples CPU/RSS every 2s while alive.
/// </summary>
public sealed class Watcher : IProcessMonitor, IAsyncDisposable
{
    private readonly Channel<ProcessEvent> _events = Channel.CreateUnbounded<ProcessEvent>();
    private readonly ConcurrentDictionary<string, LogRing> _logs = new();
    private readonly ILogger<Watcher> _logger;

    public Watcher(ILogger<Watcher> logger) { _logger = logger; }

    public ChannelReader<ProcessEvent> Events => _events.Reader;
    public IReadOnlyDictionary<string, LogRing> Logs => _logs;

    public ProcessHandle Launch(LaunchSpec spec, CancellationToken ct = default)
    {
        var ring = _logs.GetOrAdd(spec.SubprocessId, _ => new LogRing());
        var exit = RunAsync(spec, ring, ct);
        return new ProcessHandle(spec.SubprocessId, exit, ring);
    }

    private async Task<int> RunAsync(LaunchSpec spec, LogRing ring, CancellationToken ct)
    {
        var cmd = Cli.Wrap(spec.Executable).WithArguments(spec.Arguments).WithValidation(CommandResultValidation.None);
        if (spec.Env is not null)
        {
            var envNullable = spec.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            cmd = cmd.WithEnvironmentVariables(envNullable);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.Timeout is { } t) linked.CancelAfter(t);

        var at = DateTimeOffset.UtcNow;
        await _events.Writer.WriteAsync(new ProcessEvent(spec.SubprocessId, spec.Label, ProcessEventKind.Started, null, null, null, null, at), ct);

        int pid = 0;
        var sampleCts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampleCts.IsCancellationRequested)
            {
                try
                {
                    if (pid != 0)
                    {
                        using var proc = Process.GetProcessById(pid);
                        var elapsed = Math.Max(1.0, DateTime.Now.Subtract(proc.StartTime).TotalSeconds);
                        var cpuFrac = proc.TotalProcessorTime.TotalSeconds / (elapsed * Math.Max(1, Environment.ProcessorCount));
                        await _events.Writer.WriteAsync(new ProcessEvent(
                            spec.SubprocessId, spec.Label, ProcessEventKind.ResourceSample,
                            null, null, cpuFrac,
                            proc.WorkingSet64, DateTimeOffset.UtcNow));
                    }
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(2), sampleCts.Token).ContinueWith(_ => { });
            }
        }, sampleCts.Token);

        int exitCode = 0;
        try
        {
            await foreach (var ev in cmd.ListenAsync(linked.Token))
            {
                switch (ev)
                {
                    case StartedCommandEvent s:
                        pid = s.ProcessId;
                        break;
                    case StandardOutputCommandEvent o:
                        ring.Append("[OUT] " + o.Text);
                        await _events.Writer.WriteAsync(new ProcessEvent(spec.SubprocessId, spec.Label, ProcessEventKind.Stdout, o.Text, null, null, null, DateTimeOffset.UtcNow), ct);
                        break;
                    case StandardErrorCommandEvent e:
                        ring.Append("[ERR] " + e.Text);
                        await _events.Writer.WriteAsync(new ProcessEvent(spec.SubprocessId, spec.Label, ProcessEventKind.Stderr, e.Text, null, null, null, DateTimeOffset.UtcNow), ct);
                        break;
                    case ExitedCommandEvent x:
                        exitCode = x.ExitCode;
                        await _events.Writer.WriteAsync(new ProcessEvent(spec.SubprocessId, spec.Label, ProcessEventKind.Exited, null, x.ExitCode, null, null, DateTimeOffset.UtcNow), ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Subprocess {Id} timed out or cancelled", spec.SubprocessId);
            await _events.Writer.WriteAsync(new ProcessEvent(spec.SubprocessId, spec.Label, ProcessEventKind.Incident, "timeout/cancel", -1, null, null, DateTimeOffset.UtcNow), ct);
            exitCode = -1;
        }
        finally
        {
            sampleCts.Cancel();
        }

        return exitCode;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
