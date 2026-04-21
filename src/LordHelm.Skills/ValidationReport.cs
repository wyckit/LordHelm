namespace LordHelm.Skills;

public enum ValidationStage { Xsd, JsonSchema }

public sealed record ValidationError(
    ValidationStage Stage,
    string Message,
    int LineNumber,
    int LinePosition);

public sealed record ValidationReport(IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public static ValidationReport Valid { get; } = new(Array.Empty<ValidationError>());
}
