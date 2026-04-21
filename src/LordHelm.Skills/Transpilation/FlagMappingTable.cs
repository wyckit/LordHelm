namespace LordHelm.Skills.Transpilation;

/// <summary>
/// Canonical parameter name -> vendor-specific CLI flag.
/// Keyed by (vendorId, cliVersion). Missing entries mean the CLI does not support that parameter;
/// the transpiler applies a fallback shim or rejects the invocation.
/// </summary>
public sealed class FlagMappingTable
{
    private readonly Dictionary<(string vendor, string version, string canonical), string> _map = new();

    public FlagMappingTable Add(string vendor, string version, string canonical, string vendorFlag)
    {
        _map[(vendor, version, canonical)] = vendorFlag;
        return this;
    }

    public string? Lookup(string vendor, string version, string canonical)
    {
        if (_map.TryGetValue((vendor, version, canonical), out var v)) return v;
        if (_map.TryGetValue((vendor, "*", canonical), out v)) return v;
        return null;
    }

    public static FlagMappingTable Default() => new FlagMappingTable()
        .Add("claude", "*", "outputFormat", "--output-format")
        .Add("claude", "*", "model", "--model")
        .Add("claude", "*", "maxTokens", "--max-tokens")
        .Add("claude", "*", "systemPrompt", "--system-prompt")
        .Add("claude", "*", "print", "-p")
        .Add("gemini", "*", "outputFormat", "--format")
        .Add("gemini", "*", "model", "--model")
        .Add("gemini", "*", "maxTokens", "--max-output-tokens")
        .Add("codex", "*", "outputFormat", "--format")
        .Add("codex", "*", "model", "--model")
        .Add("codex", "*", "maxTokens", "--max-tokens");
}
