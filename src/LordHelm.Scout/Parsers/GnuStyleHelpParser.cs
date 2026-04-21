using System.Text.RegularExpressions;

namespace LordHelm.Scout.Parsers;

/// <summary>
/// Parses GNU-style --help output used by claude, gemini, and codex:
///   -s, --short-name &lt;value&gt;   Description
///       --flag-name=&lt;type&gt;      Description
///       --switch                Description
/// Every CLI we care about emits this shape; vendor-specific quirks are handled via overrides
/// in subclasses if needed.
/// </summary>
public class GnuStyleHelpParser : ICliHelpParser
{
    public string VendorId { get; }

    // matches: optional "-x,"  then "--long-name"  then optional "=<type>" or " <type>"  then description
    private static readonly Regex FlagLine = new(
        @"^\s*(?:-(?<short>[A-Za-z0-9]),\s+)?--(?<name>[A-Za-z0-9][A-Za-z0-9-]*)(?:[=\s](?<type>[<\[][^>\]]+[>\]]|[A-Z_]+))?\s{2,}(?<desc>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionLine = new(
        @"(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public GnuStyleHelpParser(string vendorId)
    {
        VendorId = vendorId;
    }

    public CliSpec Parse(string helpOutput, string versionOutput, DateTimeOffset capturedAt)
    {
        var flags = new List<CliFlag>();
        foreach (var rawLine in helpOutput.Split('\n'))
        {
            var m = FlagLine.Match(rawLine);
            if (!m.Success) continue;
            flags.Add(new CliFlag(
                Name: m.Groups["name"].Value,
                ShortName: m.Groups["short"].Success ? m.Groups["short"].Value : null,
                Type: m.Groups["type"].Success ? m.Groups["type"].Value.Trim('<', '>', '[', ']') : null,
                Default: null,
                Description: m.Groups["desc"].Value.Trim()));
        }

        var version = VersionLine.Match(versionOutput) is { Success: true } vm ? vm.Groups["v"].Value : "unknown";

        return new CliSpec(VendorId, version, flags, capturedAt);
    }
}
