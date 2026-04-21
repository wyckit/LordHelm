namespace LordHelm.Skills.Transpilation;

/// <summary>
/// Narrow contract the Scout project depends on for feeding fresh CLI flag lists back into
/// the transpiler's mapping table without taking a direct reference to <see cref="FlagMappingTable"/>.
/// Kept in the Skills project so Scout has no knowledge of `ImmutableDictionary` internals.
/// </summary>
public interface IScoutFlagHydrator
{
    void Hydrate(string vendor, string cliVersion, IEnumerable<(string FlagName, string? Default)> flags);
    void DropVendorVersioned(string vendor);
}

/// <summary>
/// Default adapter that forwards to a <see cref="FlagMappingTable"/> instance. The Scout
/// service receives this via DI; tests can swap it for a fake.
/// </summary>
public sealed class FlagMappingTableHydrator : IScoutFlagHydrator
{
    private readonly FlagMappingTable _table;
    public FlagMappingTableHydrator(FlagMappingTable table) { _table = table; }
    public void Hydrate(string vendor, string cliVersion, IEnumerable<(string FlagName, string? Default)> flags)
        => _table.Hydrate(vendor, cliVersion, flags);
    public void DropVendorVersioned(string vendor) => _table.DropVendorVersioned(vendor);
}
