using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public sealed record NodeRef(string Namespace, string Id, IReadOnlyDictionary<string, string> Metadata);

public sealed record NodeEvent(NodeRef Node, string Text, DateTimeOffset At);

public sealed record SubscriptionSpec(
    string Id,
    string Namespace,
    string IdPattern,
    Func<NodeRef, bool>? MetadataPredicate = null);

public interface IDataflowBus
{
    Task SubscribeAsync(SubscriptionSpec spec, Func<NodeEvent, Task> handler, CancellationToken ct = default);
    Task UnsubscribeAsync(string subscriptionId);
    Task PublishAsync(NodeEvent ev, CancellationToken ct = default);
}

/// <summary>
/// In-memory blackboard bus. Subscriptions are (namespace, id-glob, metadata-predicate) tuples;
/// when a write matches, the handler is invoked on a thread-pool task. Idempotency keyed by
/// (subscriptionId, nodeNs, nodeId, metaHash) to prevent double-fire on repeated writes.
/// </summary>
public sealed class DataflowBus : IDataflowBus
{
    private readonly ConcurrentDictionary<string, (SubscriptionSpec Spec, Func<NodeEvent, Task> Handler, Regex IdRegex)> _subs = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly ILogger<DataflowBus> _logger;

    public DataflowBus(ILogger<DataflowBus> logger) { _logger = logger; }

    public Task SubscribeAsync(SubscriptionSpec spec, Func<NodeEvent, Task> handler, CancellationToken ct = default)
    {
        var regex = new Regex("^" + Regex.Escape(spec.IdPattern).Replace("\\*", ".*") + "$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        _subs[spec.Id] = (spec, handler, regex);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string subscriptionId)
    {
        _subs.TryRemove(subscriptionId, out _);
        return Task.CompletedTask;
    }

    public async Task PublishAsync(NodeEvent ev, CancellationToken ct = default)
    {
        foreach (var (_, (spec, handler, regex)) in _subs)
        {
            if (!string.Equals(spec.Namespace, ev.Node.Namespace, StringComparison.Ordinal)) continue;
            if (!regex.IsMatch(ev.Node.Id)) continue;
            if (spec.MetadataPredicate is not null && !spec.MetadataPredicate(ev.Node)) continue;

            var dedupeKey = $"{spec.Id}|{ev.Node.Namespace}|{ev.Node.Id}|{string.Join(",", ev.Node.Metadata.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value))}";
            if (!_seen.TryAdd(dedupeKey, 0)) continue;

            _ = Task.Run(async () =>
            {
                try { await handler(ev); }
                catch (Exception ex) { _logger.LogWarning(ex, "Subscription {Sub} handler failed", spec.Id); }
            }, ct);
        }
    }
}
