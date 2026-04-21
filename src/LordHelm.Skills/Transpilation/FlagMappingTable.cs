using System.Collections.Immutable;

namespace LordHelm.Skills.Transpilation;

/// <summary>
/// Canonical parameter name -> vendor-specific CLI flag.
/// Keyed by (vendorId, cliVersion). Missing entries mean the CLI does not support that parameter;
/// the transpiler applies a fallback shim or drops the parameter.
///
/// Thread-safety: the internal dictionary is swapped atomically via <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// every time curated entries are merged with Scout-discovered flags. Readers always observe a
/// consistent snapshot — either pre-merge or post-merge, never a partial view.
/// </summary>
public sealed class FlagMappingTable
{
    private ImmutableDictionary<(string vendor, string version, string canonical), string> _map;

    public FlagMappingTable()
    {
        _map = ImmutableDictionary.Create<(string, string, string), string>();
    }

    public FlagMappingTable Add(string vendor, string version, string canonical, string vendorFlag)
    {
        // Copy-on-write swap. Safe under concurrent readers.
        var next = _map.SetItem((vendor, version, canonical), vendorFlag);
        Interlocked.Exchange(ref _map, next);
        return this;
    }

    public string? Lookup(string vendor, string version, string canonical)
    {
        // Snapshot read.
        var snapshot = _map;
        if (snapshot.TryGetValue((vendor, version, canonical), out var v)) return v;
        if (snapshot.TryGetValue((vendor, "*", canonical), out v)) return v;
        return null;
    }

    /// <summary>
    /// Merge flags from a live Scout <c>CliSpec</c> into the table for one (vendor, cliVersion) pair.
    /// The canonical-name heuristic converts kebab-case flag names into camelCase and registers them
    /// alongside curated mappings. Curated entries always win ties (they're merged last). The merge
    /// is atomic: readers never observe an intermediate state.
    /// </summary>
    public void Hydrate(string vendor, string cliVersion, IEnumerable<(string FlagName, string? Default)> flags)
    {
        var curated = _map;
        var builder = curated.ToBuilder();
        foreach (var (flagName, _) in flags)
        {
            var canonical = KebabToCamel(flagName);
            if (string.IsNullOrEmpty(canonical)) continue;
            var vendorFlag = "--" + flagName;
            var key = (vendor, cliVersion, canonical);
            // Only fill in flags we didn't already have curated entries for.
            if (!builder.ContainsKey(key) && !builder.ContainsKey((vendor, "*", canonical)))
                builder[key] = vendorFlag;
        }
        Interlocked.Exchange(ref _map, builder.ToImmutable());
    }

    /// <summary>
    /// Drop all versioned (non-wildcard) entries for a vendor. Called on Scout drift so a new
    /// <see cref="Hydrate"/> call leaves a clean slate for the new CLI version.
    /// </summary>
    public void DropVendorVersioned(string vendor)
    {
        var current = _map;
        var builder = current.ToBuilder();
        foreach (var key in current.Keys.Where(k => k.vendor == vendor && k.version != "*").ToList())
            builder.Remove(key);
        Interlocked.Exchange(ref _map, builder.ToImmutable());
    }

    private static string KebabToCamel(string kebab)
    {
        if (string.IsNullOrEmpty(kebab)) return kebab;
        var parts = kebab.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(parts[0].ToLowerInvariant());
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            sb.Append(char.ToUpperInvariant(parts[i][0]));
            if (parts[i].Length > 1) sb.Append(parts[i].Substring(1).ToLowerInvariant());
        }
        return sb.ToString();
    }

    /// <summary>Curated starting point. Populated from years of known vendor conventions.</summary>
    public static FlagMappingTable Default()
    {
        var t = new FlagMappingTable();
        t.Add("claude", "*", "outputFormat", "--output-format");
        t.Add("claude", "*", "model", "--model");
        t.Add("claude", "*", "maxTokens", "--max-tokens");
        t.Add("claude", "*", "systemPrompt", "--system-prompt");
        t.Add("claude", "*", "print", "-p");
        t.Add("gemini", "*", "outputFormat", "--format");
        t.Add("gemini", "*", "model", "--model");
        t.Add("gemini", "*", "maxTokens", "--max-output-tokens");
        t.Add("codex", "*", "outputFormat", "--format");
        t.Add("codex", "*", "model", "--model");
        t.Add("codex", "*", "maxTokens", "--max-tokens");
        return t;
    }
}
