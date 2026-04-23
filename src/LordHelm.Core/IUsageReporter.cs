namespace LordHelm.Core;

/// <summary>
/// Reports real per-call usage from an <see cref="IAgentModelAdapter"/> to
/// whatever aggregator the host wires in (today: <c>UsageAccumulator</c>
/// in LordHelm.Orchestrator.Usage). Kept in Core so the Providers layer
/// doesn't have to import Orchestrator.
/// </summary>
public interface IUsageReporter
{
    void Report(string vendorId, string modelId, UsageRecord usage, decimal costUsd);
}
