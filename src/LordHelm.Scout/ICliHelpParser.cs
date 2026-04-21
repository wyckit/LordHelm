namespace LordHelm.Scout;

public interface ICliHelpParser
{
    string VendorId { get; }
    CliSpec Parse(string helpOutput, string versionOutput, DateTimeOffset capturedAt);
}
