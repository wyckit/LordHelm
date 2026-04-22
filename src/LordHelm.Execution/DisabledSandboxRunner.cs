using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

/// <summary>
/// Null-object implementation of <see cref="ISandboxRunner"/> for running Lord Helm
/// without Docker. Every <see cref="RunAsync"/> call returns a structured
/// <see cref="SandboxResult"/> carrying exit code 126 (permission denied) and a
/// clear stderr message. This lets Host-tier skills work untouched while Docker-tier
/// skills fail fast with a diagnosable error instead of a cryptic Docker connection
/// timeout.
///
/// Selected via <c>LORDHELM_SANDBOX_MODE=disabled</c>. Default remains the real
/// <see cref="DockerSandboxRunner"/>.
/// </summary>
public sealed class DisabledSandboxRunner : ISandboxRunner
{
    private readonly ILogger<DisabledSandboxRunner>? _logger;

    public DisabledSandboxRunner(ILogger<DisabledSandboxRunner>? logger = null)
    {
        _logger = logger;
    }

    public Task<SandboxResult> RunAsync(string[] command, SandboxPolicy policy, CancellationToken ct = default)
    {
        _logger?.LogWarning(
            "Sandbox execution requested while LORDHELM_SANDBOX_MODE=disabled. " +
            "Skill will be rejected. Command (truncated): {Cmd}",
            command.Length == 0 ? "(empty)" : string.Join(' ', command).Substring(0, Math.Min(120, string.Join(' ', command).Length)));

        return Task.FromResult(new SandboxResult(
            ExitCode: 126,
            Stdout: string.Empty,
            Stderr: "Sandbox disabled (LORDHELM_SANDBOX_MODE=disabled). " +
                    "This skill declares ExecutionEnvironment=Docker and cannot run without a sandbox runner. " +
                    "Set LORDHELM_SANDBOX_MODE=docker and ensure Docker Desktop is running, or rewrite the skill as Host tier.",
            Elapsed: TimeSpan.Zero));
    }
}
