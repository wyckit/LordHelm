namespace LordHelm.Skills.Transpilation;

/// <summary>
/// Seam for external services (notably the Scout protocol) to invalidate the JIT
/// transpiler's cache when CLI capability changes are detected. Keeps the
/// Scout project free of a direct reference to <see cref="JitTranspiler"/>.
/// </summary>
public interface ITranspilerCacheInvalidator
{
    void Invalidate(string vendorId, string cliVersion);
    void InvalidateAll();
}
