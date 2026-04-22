using LordHelm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.Overseers;

public sealed class OverseerRunnerOptions
{
    /// <summary>
    /// How often the runner sweeps the registry looking for agents whose
    /// NextTickAt has passed. Finer granularity means quicker response to Bump()
    /// at the cost of more thread wakeups. 1 second is a sensible default.
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Background service that drives every registered overseer on its own cadence.
/// Every <see cref="OverseerRunnerOptions.SweepInterval"/> it checks each enabled
/// agent's <c>NextTickAt</c>; when that time has passed, the agent's
/// <c>TickAsync</c> is invoked and the result recorded. Agents are never ticked
/// concurrently with themselves (per-agent tick lock) but different agents run
/// in parallel.
/// </summary>
public sealed class OverseerRunner : BackgroundService
{
    private readonly OverseerRegistry _registry;
    private readonly IAlertTray _alerts;
    private readonly IServiceProvider _services;
    private readonly OverseerRunnerOptions _options;
    private readonly ILogger<OverseerRunner> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new();

    public OverseerRunner(
        OverseerRegistry registry,
        IAlertTray alerts,
        IServiceProvider services,
        OverseerRunnerOptions options,
        ILogger<OverseerRunner> logger)
    {
        _registry = registry;
        _alerts = alerts;
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("OverseerRunner started: {Count} agent(s) registered", _registry.Agents.Count);
        while (!ct.IsCancellationRequested)
        {
            await SweepOnceAsync(ct);
            try { await Task.Delay(_options.SweepInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Exposed for tests: walk the registry once, tick any due agents.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var agent in _registry.Agents)
        {
            if (ct.IsCancellationRequested) break;
            var state = _registry.Get(agent.Id);
            if (state is null || !state.Enabled) continue;
            if (state.NextTickAt > now) continue;

            var gate = _agentLocks.GetOrAdd(agent.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct)) continue; // already ticking
            _ = RunAgentTickAsync(agent, now, gate, ct);
        }
    }

    private async Task RunAgentTickAsync(IOverseerAgent agent, DateTimeOffset at, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            var state = _registry.Get(agent.Id) ?? throw new InvalidOperationException("lost state");
            var ctx = new OverseerContext(_alerts, _services, state.TickCount + 1, at);
            OverseerResult result;
            try
            {
                result = await agent.TickAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overseer {AgentId} threw during tick", agent.Id);
                await _alerts.PushAsync(agent.Id, AlertKind.Error,
                    $"{agent.Name} failed", ex.Message, ct);
                result = new OverseerResult(OverseerStatus.Error, ex.Message);
            }
            _registry.RecordTick(agent.Id, result, at, agent.DefaultInterval);

            if (result.Status == OverseerStatus.DoneForNow)
            {
                await _alerts.PushAsync(agent.Id, AlertKind.DoneForNow,
                    $"{agent.Name} paused", result.Message ?? "done for now", ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
