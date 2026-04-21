using LordHelm.Core;

namespace LordHelm.Execution;

public sealed record HostInvocationResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed);

public interface IHostRunner
{
    Task<HostInvocationResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed class HostRunner : IHostRunner
{
    public async Task<HostInvocationResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var psi = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {executable}");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new HostInvocationResult(p.ExitCode, stdout, stderr, sw.Elapsed);
    }
}
