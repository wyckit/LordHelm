using LordHelm.Core;
using McpEngramMemory.Core.Services.Evaluation;

namespace LordHelm.Providers.Adapters;

/// <summary>
/// Wraps the <c>codex</c> CLI as a hot-swappable <see cref="IAgentModelAdapter"/>.
/// Targets the OpenAI Codex model family; favoured for code synthesis +
/// sandbox-runner tasks where raw generation throughput matters.
/// </summary>
public sealed class CodexCliAdapter : AdapterBase
{
    public CodexCliAdapter(CodexCliModelClient cli, RateLimitGovernor? governor = null, IModelCapabilityProvider? catalog = null, IUsageReporter? usageReporter = null)
        : base(
            vendorId: "codex",
            // ChatGPT-subscription accounts don't grant access to `o4`
            // (that requires an API key). gpt-5.4 is the current frontier
            // model the subscription does unlock — matches the user's
            // `codex /model` output as the "(current)" entry.
            defaultModel: "gpt-5.4",
            capabilities: new AdapterCapabilities(
                SupportedTasks: new[] { "code", "refactor", "sandbox-exec", "test-gen" },
                MaxContextTokens: 200_000,
                SupportsToolCalls: true,
                SupportsStreaming: true,
                SupportsJsonMode: true,
                Mode: ResourceMode.Builder,
                Cost: new CostProfile(InputPerMTokens: 10m, OutputPerMTokens: 40m, CacheReadPerMTokens: 1m)),
            cli: cli,
            governor: governor ?? new RateLimitGovernor(60, TimeSpan.FromMinutes(1)),
            catalog: catalog,
            usageReporter: usageReporter)
    { }
}
