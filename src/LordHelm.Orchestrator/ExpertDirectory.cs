namespace LordHelm.Orchestrator;

public sealed record ExpertPersona(
    string Id,
    string Name,
    string PreferredVendor,
    string Model,
    string SystemHint,
    IReadOnlyList<string> PreferredSkills)
{
    public string SpawnInstructions(string goal) =>
        $"You are {Name}. {SystemHint}\n\nTask: {goal}\n\nRespond with the task output only — no preamble.";
}

/// <summary>
/// Registry of specialised Expert personas that the Manager can provision on demand.
/// A persona bundles (name, preferred vendor, model, system hint, preferred skills)
/// so the same "Code Auditor" behaves the same every time it is spawned.
///
/// New personas can be registered at composition time; a future revision will load
/// them from engram so a running Lord Helm can grow its expert roster without restart.
/// </summary>
public sealed class ExpertDirectory
{
    private readonly Dictionary<string, ExpertPersona> _personas =
        new(StringComparer.OrdinalIgnoreCase);

    public ExpertDirectory Register(ExpertPersona persona)
    {
        _personas[persona.Id] = persona;
        return this;
    }

    public ExpertPersona? Get(string id) =>
        _personas.TryGetValue(id, out var p) ? p : null;

    public IReadOnlyList<ExpertPersona> All() => _personas.Values.ToList();

    /// <summary>Built-in persona roster. Extended via <see cref="Register"/> at startup.</summary>
    public static ExpertDirectory Default() => new ExpertDirectory()
        .Register(new ExpertPersona(
            Id: "code-auditor",
            Name: "Code Auditor",
            PreferredVendor: "claude",
            Model: "claude-opus-4-7",
            SystemHint: "You review code for correctness, security, performance, and maintainability. Cite specific file:line locations.",
            PreferredSkills: new[] { "read-file" }))
        .Register(new ExpertPersona(
            Id: "tech-writer",
            Name: "Tech Writer",
            PreferredVendor: "claude",
            Model: "claude-opus-4-7",
            SystemHint: "You produce clear, concise technical documentation. Use Markdown. Prefer tables and bullet lists over prose walls.",
            PreferredSkills: new[] { "read-file", "write-engram-node" }))
        .Register(new ExpertPersona(
            Id: "security-analyst",
            Name: "Security Analyst",
            PreferredVendor: "gemini",
            Model: "gemini-2.5-pro",
            SystemHint: "You hunt for security vulnerabilities: injection, secret exposure, unsafe defaults, permission escalation, supply-chain risk.",
            PreferredSkills: new[] { "read-file" }))
        .Register(new ExpertPersona(
            Id: "refactor-engineer",
            Name: "Refactor Engineer",
            PreferredVendor: "claude",
            Model: "claude-opus-4-7",
            SystemHint: "You propose concrete refactors that reduce complexity without changing behavior. Output diffs or replacement blocks.",
            PreferredSkills: new[] { "read-file" }))
        .Register(new ExpertPersona(
            Id: "sandbox-runner",
            Name: "Sandbox Runner",
            PreferredVendor: "codex",
            Model: "o4",
            SystemHint: "You execute code in the Docker sandbox and report stdout + exit code. Never run code on the host.",
            PreferredSkills: new[] { "execute-python" }))
        .Register(new ExpertPersona(
            Id: "synthesiser",
            Name: "Synthesiser",
            PreferredVendor: "claude",
            Model: "claude-opus-4-7",
            SystemHint: "You merge multiple expert outputs into a single coherent answer for the user. Preserve concrete citations and flag disagreements explicitly.",
            PreferredSkills: Array.Empty<string>()));
}
