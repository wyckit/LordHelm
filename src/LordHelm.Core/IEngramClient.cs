namespace LordHelm.Core;

/// <summary>
/// Thin facade over McpEngramMemory.Core. Defined in LordHelm.Core so every layer
/// can depend on the abstraction without pulling the engram NuGet/project reference.
/// Implemented by <c>EngramClient</c> in LordHelm.Orchestrator.
/// </summary>
public interface IEngramClient
{
    /// <summary>Upsert a memory node and optionally link category metadata.</summary>
    Task StoreAsync(
        string @namespace,
        string id,
        string text,
        string? category = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Return the top-k engram memories matching <paramref name="text"/>.</summary>
    Task<IReadOnlyList<EngramHit>> SearchAsync(
        string @namespace,
        string text,
        int k = 5,
        CancellationToken ct = default);

    /// <summary>Fetch a single memory node by id, or null if not present.</summary>
    Task<EngramHit?> GetAsync(string @namespace, string id, CancellationToken ct = default);

    /// <summary>True when the underlying engram system is reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public sealed record EngramHit(string Namespace, string Id, string Text, double Score, IReadOnlyDictionary<string, string> Metadata);
