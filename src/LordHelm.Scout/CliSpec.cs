namespace LordHelm.Scout;

public sealed record CliFlag(
    string Name,
    string? ShortName,
    string? Type,
    string? Default,
    string? Description);

public sealed record CliSpec(
    string VendorId,
    string Version,
    IReadOnlyList<CliFlag> Flags,
    DateTimeOffset CapturedAt)
{
    public string FlagDigest
    {
        get
        {
            var sorted = Flags.OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name}|{f.ShortName}|{f.Type}|{f.Default}")
                .ToArray();
            var joined = string.Join("\n", sorted);
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexStringLower(hash);
        }
    }
}

public enum MutationKind { Added, Removed, ChangedDefault, ChangedType, Promoted, Archived }

public sealed record MutationEvent(
    string VendorId,
    string FromVersion,
    string ToVersion,
    MutationKind Kind,
    string FlagName,
    string? Detail,
    DateTimeOffset At);
