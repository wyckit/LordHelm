namespace LordHelm.Core;

public enum ArtifactKind
{
    /// <summary>Plain text or log output.</summary>
    Text,
    /// <summary>Markdown-rendered report; UI picks a markdown renderer.</summary>
    Markdown,
    /// <summary>Unified-diff patch; UI highlights additions/deletions.</summary>
    Diff,
    /// <summary>JSON blob; UI pretty-prints.</summary>
    Json,
    /// <summary>Tabular data (csv/tsv); UI can render a table preview.</summary>
    Table,
    /// <summary>External URL (PR, issue, dashboard); UI renders as a link card.</summary>
    Link,
    /// <summary>Raster image (png/jpg/svg); UI renders inline.</summary>
    Image,
    /// <summary>Binary file (pdf/zip/etc.); UI renders a download chip.</summary>
    File,
    /// <summary>Code snippet with a language tag for syntax highlighting.</summary>
    Code,
}

/// <summary>
/// Structured artifact returned alongside an agent's text output. Panel-endorsed
/// shape (session debate-lordhelm-artifact-channel-2026-04-21): dual disk+engram
/// persistence — text-y kinds (Text/Markdown/Diff/Json/Table/Link/Code) ride
/// inline in <see cref="InlineBody"/> and are mirrored to the expert's engram
/// namespace so future agents can recall them; binary kinds (Image/File) are
/// persisted to <c>data/artifacts/{goalId}/{artifactId}.{ext}</c> and surface
/// only as <see cref="DiskPath"/>. <see cref="Redacted"/> lets the producer
/// flag sensitive content — UI shows a masked preview with an expand affordance.
/// </summary>
public sealed record ArtifactEntry(
    string Id,
    ArtifactKind Kind,
    string MimeType,
    string Title,
    string? InlineBody,
    string? DiskPath,
    string ProducedBy,
    DateTimeOffset ProducedAt,
    long? SizeBytes = null,
    string? Language = null,
    bool Redacted = false,
    IReadOnlyDictionary<string, string>? Metadata = null);
