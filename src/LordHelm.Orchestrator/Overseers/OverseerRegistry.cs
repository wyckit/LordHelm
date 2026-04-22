using System.Collections.Concurrent;

namespace LordHelm.Orchestrator.Overseers;

public sealed record OverseerState(
    string AgentId,
    bool Enabled,
    OverseerStatus LastStatus,
    string? LastMessage,
    DateTimeOffset? LastTickAt,
    DateTimeOffset NextTickAt,
    int TickCount,
    int FailureStreak);

/// <summary>
/// Tracks runtime state for every registered <see cref="IOverseerAgent"/>. The
/// runner queries this to decide which agents to tick; operators flip
/// <c>Enabled</c> via the UI. External event handlers can call <see cref="Bump"/>
/// to schedule an immediate next tick (overriding the agent's default cadence).
/// </summary>
public sealed class OverseerRegistry
{
    private readonly ConcurrentDictionary<string, IOverseerAgent> _agents = new();
    private readonly ConcurrentDictionary<string, OverseerState> _state = new();

    public event Action? OnChanged;

    public void Register(IOverseerAgent agent, bool enabledByDefault = true)
    {
        _agents[agent.Id] = agent;
        _state[agent.Id] = new OverseerState(
            AgentId: agent.Id,
            Enabled: enabledByDefault,
            LastStatus: OverseerStatus.WaitingForAttention,
            LastMessage: null,
            LastTickAt: null,
            NextTickAt: DateTimeOffset.UtcNow,
            TickCount: 0,
            FailureStreak: 0);
        OnChanged?.Invoke();
    }

    public IReadOnlyList<IOverseerAgent> Agents => _agents.Values.ToList();

    public OverseerState? Get(string agentId) =>
        _state.TryGetValue(agentId, out var s) ? s : null;

    public IReadOnlyList<OverseerState> Snapshot() =>
        _state.Values.OrderBy(s => s.AgentId).ToList();

    public void SetEnabled(string agentId, bool enabled)
    {
        _state.AddOrUpdate(agentId,
            _ => throw new KeyNotFoundException(agentId),
            (_, s) => s with
            {
                Enabled = enabled,
                NextTickAt = enabled ? DateTimeOffset.UtcNow : s.NextTickAt,
            });
        OnChanged?.Invoke();
    }

    /// <summary>Force the agent's next tick to happen on the runner's next sweep.</summary>
    public void Bump(string agentId)
    {
        _state.AddOrUpdate(agentId,
            _ => throw new KeyNotFoundException(agentId),
            (_, s) => s with { NextTickAt = DateTimeOffset.UtcNow });
        OnChanged?.Invoke();
    }

    public void RecordTick(string agentId, OverseerResult result, DateTimeOffset at, TimeSpan fallbackInterval)
    {
        _state.AddOrUpdate(agentId,
            _ => throw new KeyNotFoundException(agentId),
            (_, s) =>
            {
                var interval = result.NextIntervalOverride ?? result.Status switch
                {
                    OverseerStatus.Working => fallbackInterval,
                    OverseerStatus.WaitingForAttention => fallbackInterval * 2,
                    OverseerStatus.DoneForNow => TimeSpan.FromDays(365),   // effectively paused
                    OverseerStatus.Error => Backoff(s.FailureStreak + 1),
                    _ => fallbackInterval,
                };
                var nextTick = at + interval;
                var failureStreak = result.Status == OverseerStatus.Error ? s.FailureStreak + 1 : 0;
                return s with
                {
                    LastStatus = result.Status,
                    LastMessage = result.Message,
                    LastTickAt = at,
                    NextTickAt = nextTick,
                    TickCount = s.TickCount + 1,
                    FailureStreak = failureStreak,
                };
            });
        OnChanged?.Invoke();
    }

    private static TimeSpan Backoff(int streak) =>
        TimeSpan.FromSeconds(Math.Min(600, Math.Pow(2, Math.Clamp(streak, 1, 10)) * 5));
}
