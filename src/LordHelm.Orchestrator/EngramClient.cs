using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

/// <summary>
/// Reference implementation of <see cref="IEngramClient"/>. In-process placeholder
/// that buffers writes when the underlying McpEngramMemory service is unavailable.
/// The real engram integration is added by subclassing or replacing this service
/// at composition time with one that wires the MCP transport. This default keeps
/// Lord Helm runnable without a live engram server in dev/demo mode.
/// </summary>
public sealed class EngramClient : IEngramClient
{
    private readonly ILogger<EngramClient> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Ns, string Id), EngramHit> _local = new();

    public EngramClient(ILogger<EngramClient> logger)
    {
        _logger = logger;
    }

    public Task StoreAsync(string @namespace, string id, string text, string? category = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var meta = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (category is not null) meta["category"] = category;
        _local[(@namespace, id)] = new EngramHit(@namespace, id, text, 1.0, meta);
        _logger.LogDebug("engram.store ns={Ns} id={Id} bytes={Len}", @namespace, id, text.Length);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EngramHit>> SearchAsync(string @namespace, string text, int k = 5, CancellationToken ct = default)
    {
        var tokens = Tokenize(text);
        IReadOnlyList<EngramHit> hits = _local.Values
            .Where(h => string.Equals(h.Namespace, @namespace, StringComparison.Ordinal))
            .Select(h => h with { Score = Jaccard(tokens, Tokenize(h.Text)) })
            .OrderByDescending(h => h.Score)
            .Take(k)
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<EngramHit?> GetAsync(string @namespace, string id, CancellationToken ct = default)
    {
        _local.TryGetValue((@namespace, id), out var hit);
        return Task.FromResult<EngramHit?>(hit);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    private static HashSet<string> Tokenize(string text) =>
        new(text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant()), StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var inter = a.Intersect(b).Count();
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
