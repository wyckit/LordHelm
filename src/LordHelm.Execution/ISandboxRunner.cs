using LordHelm.Core;

namespace LordHelm.Execution;

public interface ISandboxRunner
{
    Task<SandboxResult> RunAsync(string[] command, SandboxPolicy policy, CancellationToken ct = default);
}
