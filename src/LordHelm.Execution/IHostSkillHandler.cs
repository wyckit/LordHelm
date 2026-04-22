using System.Text.Json;
using LordHelm.Core;

namespace LordHelm.Execution;

/// <summary>
/// In-process handler for a Host-tier skill whose implementation is a native
/// C# callback rather than a CLI subprocess. When the <see cref="ExecutionRouter"/>
/// receives a Host skill, it asks every registered <see cref="IHostSkillHandler"/>
/// whether it <see cref="Handles"/> the skill id; the first match invokes
/// <see cref="RunAsync"/> directly and the transpiler + <see cref="IHostRunner"/>
/// are bypassed. Lets us attach Lord-Helm-native skills (Roslyn scripting,
/// engram queries, skill-registry introspection) without spawning a subprocess.
/// </summary>
public interface IHostSkillHandler
{
    bool Handles(string skillId);
    Task<HostInvocationResult> RunAsync(SkillManifest skill, JsonDocument args, ExpertProfile caller, CancellationToken ct);
}
