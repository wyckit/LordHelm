using LordHelm.Core;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Wraps the <c>claude</c> CLI (Claude Code) as a hot-swappable
/// <see cref="IAgentModelAdapter"/>. Defaults reflect the Opus 4.7 1M profile:
/// large context, tool-call-capable, Interactive-mode pricing.
/// </summary>
public sealed class ClaudeCodeAdapter : AdapterBase
{
    public ClaudeCodeAdapter(ClaudeCliModelClient cli, RateLimitGovernor? governor = null, IModelCapabilityProvider? catalog = null, IUsageReporter? usageReporter = null)
        : base(
            vendorId: "claude",
            defaultModel: "claude-opus-4-7",
            capabilities: new AdapterCapabilities(
                SupportedTasks: new[] { "code", "reasoning", "review", "architecture", "docs" },
                MaxContextTokens: 1_000_000,
                SupportsToolCalls: true,
                SupportsStreaming: true,
                SupportsJsonMode: true,
                Mode: ResourceMode.Interactive,
                Cost: new CostProfile(InputPerMTokens: 15m, OutputPerMTokens: 75m, CacheReadPerMTokens: 1.5m)),
            cli: cli,
            governor: governor ?? new RateLimitGovernor(60, TimeSpan.FromMinutes(1)),
            catalog: catalog,
            usageReporter: usageReporter)
    { }
}
