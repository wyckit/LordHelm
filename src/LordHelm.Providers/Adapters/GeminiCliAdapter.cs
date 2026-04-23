using LordHelm.Core;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Wraps the <c>gemini</c> CLI as a hot-swappable <see cref="IAgentModelAdapter"/>.
/// Gemini 2.5 Pro profile: 1M-context research + multimodal; positioned for
/// Batch workloads where broad context recall matters more than latency.
/// </summary>
public sealed class GeminiCliAdapter : AdapterBase
{
    public GeminiCliAdapter(GeminiCliModelClient cli, RateLimitGovernor? governor = null, IModelCapabilityProvider? catalog = null, IUsageReporter? usageReporter = null)
        : base(
            vendorId: "gemini",
            defaultModel: "gemini-2.5-pro",
            capabilities: new AdapterCapabilities(
                SupportedTasks: new[] { "research", "multimodal", "long-context", "summarisation", "security-review" },
                MaxContextTokens: 1_000_000,
                SupportsToolCalls: true,
                SupportsStreaming: true,
                SupportsJsonMode: true,
                Mode: ResourceMode.Batch,
                Cost: new CostProfile(InputPerMTokens: 1.25m, OutputPerMTokens: 10m, CacheReadPerMTokens: 0.3125m)),
            cli: cli,
            governor: governor ?? new RateLimitGovernor(60, TimeSpan.FromMinutes(1)),
            catalog: catalog,
            usageReporter: usageReporter)
    { }
}
