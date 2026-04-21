namespace LordHelm.Execution;

public sealed record SandboxPolicy(
    string ImageRefWithDigest,
    long MemoryBytes = 512L * 1024 * 1024,
    long NanoCpus = 1_000_000_000L,
    long PidsLimit = 64,
    bool NetworkDisabled = true,
    bool ReadonlyRootfs = true,
    IReadOnlyDictionary<string, string>? TmpfsMounts = null,
    IReadOnlyList<string>? ReadOnlyBinds = null,
    TimeSpan? WallClockTimeout = null)
{
    public static SandboxPolicy Default(string imageRefWithDigest) => new(
        ImageRefWithDigest: imageRefWithDigest,
        TmpfsMounts: new Dictionary<string, string> { ["/work"] = "rw,noexec,nosuid,size=64m" },
        ReadOnlyBinds: Array.Empty<string>(),
        WallClockTimeout: TimeSpan.FromMinutes(2));
}
