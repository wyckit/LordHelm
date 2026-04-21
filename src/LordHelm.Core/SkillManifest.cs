namespace LordHelm.Core;

public sealed record SemVer(int Major, int Minor, int Patch, string? Prerelease = null)
{
    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public static SemVer Parse(string s)
    {
        var preSplit = s.Split('-', 2);
        var parts = preSplit[0].Split('.');
        if (parts.Length != 3) throw new FormatException($"Invalid semver: {s}");
        return new SemVer(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            preSplit.Length == 2 ? preSplit[1] : null);
    }
}

public sealed record SkillManifest(
    string Id,
    SemVer Version,
    string ContentHashSha256,
    ExecutionEnvironment ExecEnv,
    bool RequiresApproval,
    RiskTier RiskTier,
    TimeSpan Timeout,
    TrustLevel MinTrust,
    string ParameterSchemaJson,
    string CanonicalXml);
