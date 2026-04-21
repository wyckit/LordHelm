using System.Collections.Concurrent;

namespace LordHelm.Consensus;

/// <summary>
/// In-memory novelty check using token-overlap Jaccard similarity as a stand-in for
/// embedding-based cosine. Replace the implementation with an engram-backed semantic
/// recall when running in production; the interface and threshold are kept the same.
/// </summary>
public sealed class TokenOverlapNoveltyCheck : INoveltyCheck
{
    private readonly ConcurrentBag<string> _prior = new();
    public double Threshold { get; set; } = 0.85;

    public void RememberPriorFailure(string fix) => _prior.Add(fix);

    public Task<bool> IsNovelAsync(string proposedFix, CancellationToken ct = default)
    {
        var ptokens = Tokenize(proposedFix);
        foreach (var prior in _prior)
        {
            var score = Jaccard(ptokens, Tokenize(prior));
            if (score >= Threshold) return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private static HashSet<string> Tokenize(string text) =>
        new(text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()), StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var inter = a.Intersect(b).Count();
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
