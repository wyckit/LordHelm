using System.Collections.Concurrent;
using System.Text.Json;
using LordHelm.Core;

namespace LordHelm.Skills.Transpilation;

public sealed record TranspiledInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Env);

public interface IJitTranspiler
{
    TranspiledInvocation Transpile(SkillManifest skill, JsonDocument args, string vendorId, string cliVersion, TargetShell shell);
    void Invalidate(string vendorId, string cliVersion);
    void InvalidateAll();
}

public sealed class JitTranspiler : IJitTranspiler, ITranspilerCacheInvalidator
{
    private readonly FlagMappingTable _map;
    private readonly ConcurrentDictionary<(string skillHash, string vendor, string version, TargetShell shell), TranspiledInvocation> _cache = new();

    public JitTranspiler(FlagMappingTable? map = null)
    {
        _map = map ?? FlagMappingTable.Default();
    }

    public TranspiledInvocation Transpile(SkillManifest skill, JsonDocument args, string vendorId, string cliVersion, TargetShell shell)
    {
        var key = (skill.ContentHashSha256, vendorId, cliVersion, shell);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var argList = new List<string>();
        foreach (var prop in args.RootElement.EnumerateObject())
        {
            var flag = _map.Lookup(vendorId, cliVersion, prop.Name);
            if (flag is null)
            {
                continue;
            }
            var value = RenderValue(prop.Value);
            if (value is null)
            {
                argList.Add(ShellEscaper.Escape(flag, shell));
            }
            else
            {
                argList.Add(ShellEscaper.Escape(flag, shell));
                argList.Add(ShellEscaper.Escape(value, shell));
            }
        }

        var inv = new TranspiledInvocation(vendorId, argList, new Dictionary<string, string>());
        _cache[key] = inv;
        return inv;
    }

    public void Invalidate(string vendorId, string cliVersion)
    {
        foreach (var key in _cache.Keys.Where(k => k.vendor == vendorId && (k.version == cliVersion || cliVersion == "*")).ToList())
            _cache.TryRemove(key, out _);
    }

    public void InvalidateAll() => _cache.Clear();

    private static string? RenderValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => null,
        JsonValueKind.Null => null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.Array or JsonValueKind.Object => el.GetRawText(),
        _ => null,
    };
}
