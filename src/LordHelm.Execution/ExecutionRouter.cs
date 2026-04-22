using System.Text.Json;
using LordHelm.Core;
using LordHelm.Skills.Transpilation;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

public sealed record ToolInvocationResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    ExecutionEnvironment RoutedTo,
    bool Approved);

public interface IExecutionRouter
{
    Task<ToolInvocationResult> RouteAsync(SkillManifest skill, JsonDocument args, ExpertProfile caller, string cliVersion, TargetShell shell, CancellationToken ct = default);
}

public sealed class ExecutionRouter : IExecutionRouter
{
    private readonly IJitTranspiler _transpiler;
    private readonly ISandboxRunner _sandbox;
    private readonly IHostRunner _host;
    private readonly IApprovalGate _gate;
    private readonly Func<SkillManifest, SandboxPolicy> _policyFor;
    private readonly IReadOnlyList<IHostSkillHandler> _hostSkillHandlers;
    private readonly ILogger<ExecutionRouter> _logger;

    public ExecutionRouter(
        IJitTranspiler transpiler,
        ISandboxRunner sandbox,
        IHostRunner host,
        IApprovalGate gate,
        Func<SkillManifest, SandboxPolicy> policyFor,
        IEnumerable<IHostSkillHandler> hostSkillHandlers,
        ILogger<ExecutionRouter> logger)
    {
        _transpiler = transpiler;
        _sandbox = sandbox;
        _host = host;
        _gate = gate;
        _policyFor = policyFor;
        _hostSkillHandlers = hostSkillHandlers.ToList();
        _logger = logger;
    }

    public async Task<ToolInvocationResult> RouteAsync(SkillManifest skill, JsonDocument args, ExpertProfile caller, string cliVersion, TargetShell shell, CancellationToken ct = default)
    {
        if (skill.RequiresApproval || skill.RiskTier != RiskTier.Read)
        {
            var decision = await _gate.RequestAsync(new HostActionRequest(
                skill.Id, skill.RiskTier,
                Summary: $"{caller.ExpertId} -> {skill.Id}",
                DiffPreview: null,
                OperatorId: caller.ExpertId,
                SessionId: caller.GoalContext), ct);
            if (!decision.Approved)
            {
                return new ToolInvocationResult(-1, string.Empty, decision.Reason, TimeSpan.Zero, skill.ExecEnv, false);
            }
        }

        // Native in-process handler — bypasses the transpiler + HostRunner.
        // Used for Lord-Helm-native skills (Roslyn scripting, engram queries)
        // that aren't a CLI invocation at all.
        if (skill.ExecEnv == ExecutionEnvironment.Host)
        {
            var handler = _hostSkillHandlers.FirstOrDefault(h => h.Handles(skill.Id));
            if (handler is not null)
            {
                _logger.LogInformation("Skill {Skill} handled in-process by {Handler}", skill.Id, handler.GetType().Name);
                var direct = await handler.RunAsync(skill, args, caller, ct);
                return new ToolInvocationResult(direct.ExitCode, direct.Stdout, direct.Stderr, direct.Elapsed, ExecutionEnvironment.Host, true);
            }
        }

        var inv = _transpiler.Transpile(skill, args, caller.CliVendorId, cliVersion, shell);

        return skill.ExecEnv switch
        {
            ExecutionEnvironment.Host => await RouteHostAsync(skill, inv, ct),
            ExecutionEnvironment.Docker => await RouteDockerAsync(skill, inv, ct),
            ExecutionEnvironment.Remote => throw new NotSupportedException("Remote execution not yet implemented."),
            _ => throw new InvalidOperationException($"Unknown ExecutionEnvironment: {skill.ExecEnv}"),
        };
    }

    private async Task<ToolInvocationResult> RouteHostAsync(SkillManifest skill, TranspiledInvocation inv, CancellationToken ct)
    {
        var result = await _host.RunAsync(inv.Executable, inv.Arguments, ct);
        return new ToolInvocationResult(result.ExitCode, result.Stdout, result.Stderr, result.Elapsed, ExecutionEnvironment.Host, true);
    }

    private async Task<ToolInvocationResult> RouteDockerAsync(SkillManifest skill, TranspiledInvocation inv, CancellationToken ct)
    {
        var policy = _policyFor(skill);
        var cmd = new[] { inv.Executable }.Concat(inv.Arguments).ToArray();
        var result = await _sandbox.RunAsync(cmd, policy, ct);
        return new ToolInvocationResult(result.ExitCode, result.Stdout, result.Stderr, result.Elapsed, ExecutionEnvironment.Docker, true);
    }
}
