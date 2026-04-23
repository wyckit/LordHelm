using LordHelm.Consensus;
using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Monitor;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.ModelDiscovery;
using LordHelm.Orchestrator.Overseers;
using LordHelm.Providers;
using LordHelm.Providers.Adapters;
using LordHelm.Scout;
using LordHelm.Scout.Parsers;
using LordHelm.Skills;
using LordHelm.Skills.Transpilation;
using LordHelm.Web;
using LordHelm.Web.Components;
using McpEngramMemory.Core.Services.Evaluation;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- state paths
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var skillsDbPath = Path.Combine(dataDir, "skills.db");
var cliSpecDbPath = Path.Combine(dataDir, "cli_specs.db");
var auditDbPath = Path.Combine(dataDir, "audit.db");

// ---------------------------------------------------------------- UI
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<WidgetState>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Topology.TopologyState>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Topology.IFleetTaskSource, LordHelm.Web.Topology.WidgetStateFleetTaskSource>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Topology.DataflowTracker>();
builder.Services.AddHostedService<LordHelm.Orchestrator.Topology.TopologyProjectionService>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Artifacts.IArtifactStore>(sp =>
{
    var root = Path.Combine(dataDir, "artifacts");
    return new LordHelm.Orchestrator.Artifacts.FileArtifactStore(
        root,
        sp.GetRequiredService<ILogger<LordHelm.Orchestrator.Artifacts.FileArtifactStore>>(),
        sp.GetService<IEngramClient>());
});
builder.Services.AddSingleton<LordHelm.Web.Layout.DashboardLayoutState>();
builder.Services.AddSingleton<LordHelm.Web.Layout.IDashboardLayoutStore>(sp =>
{
    var path = Path.Combine(dataDir, "dashboard-layout.json");
    return new LordHelm.Web.Layout.JsonFileDashboardLayoutStore(path,
        sp.GetRequiredService<ILogger<LordHelm.Web.Layout.JsonFileDashboardLayoutStore>>());
});
builder.Services.AddHostedService<LordHelm.Web.Layout.DashboardLayoutPersistenceHostedService>();
builder.Services.AddSingleton<LordHelm.Web.Layout.LayoutPresetResolver>();

// ---------------------------------------------------------------- engram facade
// Default: in-process fallback client. To opt in to live MCP transport, set
// LORDHELM_ENGRAM_MCP=true and provide the server command/args via config.
var useMcp = string.Equals(builder.Configuration["LORDHELM_ENGRAM_MCP"]
    ?? Environment.GetEnvironmentVariable("LORDHELM_ENGRAM_MCP"), "true", StringComparison.OrdinalIgnoreCase);
if (useMcp)
{
    builder.Services.AddSingleton<McpEngramOptions>();
    builder.Services.AddSingleton<McpEngramClient>();
    builder.Services.AddSingleton<IEngramClient>(sp => sp.GetRequiredService<McpEngramClient>());
    builder.Services.AddHostedService<McpEngramConnector>();
}
else
{
    builder.Services.AddSingleton<IEngramClient, EngramClient>();
}

// ---------------------------------------------------------------- skills
builder.Services.AddSingleton<ISkillCache>(_ => new SqliteSkillCache(skillsDbPath));
builder.Services.AddSingleton<ManifestValidator>();
builder.Services.AddSingleton<ISkillLoader, SkillLoader>();
// Skill exporters — Lord Helm pushes its manifests into each CLI's native
// instruction-file directory so the upstream agent can invoke the same
// skill library Lord Helm orchestrates. Different CLIs have different
// formats: Claude = SKILL.md with YAML frontmatter, Codex = plain
// markdown prompts, Gemini = command markdown. Exporters handle the shape
// conversion; the operator clicks one button per vendor on /skills.
builder.Services.AddSingleton<LordHelm.Skills.Export.ISkillExporter, LordHelm.Skills.Export.ClaudeSkillExporter>();
builder.Services.AddSingleton<LordHelm.Skills.Export.ISkillExporter, LordHelm.Skills.Export.CodexPromptExporter>();
builder.Services.AddSingleton<LordHelm.Skills.Export.ISkillExporter, LordHelm.Skills.Export.GeminiCommandExporter>();
builder.Services.AddSingleton<ISkillAuthor>(sp =>
{
    var skillsDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "skills"));
    return new SkillAuthor(
        sp.GetRequiredService<ManifestValidator>(),
        sp.GetRequiredService<ISkillCache>(),
        skillsDir);
});

// ---------------------------------------------------------------- transpiler + flag table
builder.Services.AddSingleton<FlagMappingTable>(_ => FlagMappingTable.Default());
builder.Services.AddSingleton<IScoutFlagHydrator, FlagMappingTableHydrator>();
builder.Services.AddSingleton<JitTranspiler>(sp => new JitTranspiler(sp.GetRequiredService<FlagMappingTable>()));
builder.Services.AddSingleton<IJitTranspiler>(sp => sp.GetRequiredService<JitTranspiler>());
builder.Services.AddSingleton<ITranspilerCacheInvalidator>(sp => sp.GetRequiredService<JitTranspiler>());

// ---------------------------------------------------------------- scout
builder.Services.AddSingleton<ICliSpecStore>(_ => new SqliteCliSpecStore(cliSpecDbPath));
builder.Services.AddSingleton(sp => new ScoutOptions
{
    Interval = TimeSpan.FromMinutes(30),
    ProbeTimeout = TimeSpan.FromSeconds(8),
    StabilityThreshold = 3,
    Targets = new[]
    {
        new ScoutTarget("claude", "claude", new GnuStyleHelpParser("claude")),
        new ScoutTarget("gemini", "gemini", new GnuStyleHelpParser("gemini")),
        new ScoutTarget("codex",  "codex",  new GnuStyleHelpParser("codex")),
    },
    FlagHydrator = sp.GetRequiredService<IScoutFlagHydrator>(),
});
builder.Services.AddSingleton<ScoutService>(sp => new ScoutService(
    sp.GetRequiredService<ScoutOptions>(),
    sp.GetRequiredService<ICliSpecStore>(),
    sp.GetRequiredService<ILogger<ScoutService>>(),
    onMutation: ev =>
    {
        // 1. Invalidate the transpiler cache for that vendor (existing behavior).
        sp.GetRequiredService<ITranspilerCacheInvalidator>().Invalidate(ev.VendorId, ev.ToVersion);
        // 2. Event-driven model refresh — the CLI's flag/version surface
        //    just changed for this vendor, so its model list might have
        //    changed too. Fire-and-forget so Scout's own loop isn't blocked.
        var refresher = sp.GetService<ModelCatalogRefresher>();
        if (refresher is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await refresher.RefreshVendorAsync(ev.VendorId); }
                catch { /* refresher logs its own failures */ }
            });
        }
    }));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScoutService>());

// ---------------------------------------------------------------- execution
builder.Services.AddSingleton<IAuditLog>(_ => new SqliteAuditLog(auditDbPath));
builder.Services.AddSingleton<ApprovalGate>();
builder.Services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<ApprovalGate>());
builder.Services.AddSingleton<IHostRunner, HostRunner>();

// LORDHELM_SANDBOX_MODE: `docker` (default) or `disabled`. When disabled the
// DisabledSandboxRunner returns a structured "sandbox disabled" error for any
// Docker-tier skill. Host-tier skills remain untouched. No Docker daemon or
// Docker.DotNet connection is required in disabled mode.
var sandboxMode = (builder.Configuration["LORDHELM_SANDBOX_MODE"]
    ?? Environment.GetEnvironmentVariable("LORDHELM_SANDBOX_MODE")
    ?? "docker").Trim().ToLowerInvariant();
if (sandboxMode is "disabled" or "off" or "none")
{
    builder.Services.AddSingleton<ISandboxRunner, DisabledSandboxRunner>();
}
else
{
    builder.Services.AddSingleton<ISandboxRunner>(sp =>
        DockerSandboxRunner.CreateDefault(sp.GetRequiredService<ILogger<DockerSandboxRunner>>()));
}
// Per-skill Docker image map. Picks a sensible base image given the skill id.
var sandboxImagesByTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["execute-python"]  = "python:3.12-slim",
    ["execute-csharp"]  = "mcr.microsoft.com/dotnet/sdk:9.0",
};
builder.Services.AddSingleton<Func<SkillManifest, SandboxPolicy>>(_ => skill =>
{
    var baseImage = sandboxImagesByTag.TryGetValue(skill.Id, out var img)
        ? img
        : "python:3.12-slim";
    return SandboxPolicy.Default(baseImage + "@sha256:0000000000000000000000000000000000000000000000000000000000000000");
});
builder.Services.AddSingleton<IHostSkillHandler, CSharpScriptHostHandler>();
builder.Services.AddSingleton<IHostSkillHandler, FileSystemHostHandler>();
builder.Services.AddSingleton<IExecutionRouter, ExecutionRouter>();

// ---------------------------------------------------------------- providers
builder.Services.AddSingleton<ClaudeCliModelClient>();
builder.Services.AddSingleton<GeminiCliModelClient>();
builder.Services.AddSingleton<CodexCliModelClient>();

// Agent adapter seam — Claude/Codex/Gemini as hot-swappable IAgentModelAdapter.
// The registry enumerates them; AdapterRouter scores them per-request using
// configurable RouterWeights (persisted to data/routing-weights.json);
// AdapterProviderOrchestrator exposes the legacy IProviderOrchestrator surface.
// Each adapter also receives the IModelCapabilityProvider so different model
// ids under the same vendor can override context/cost/mode at catalog level.
builder.Services.AddSingleton<IAgentModelAdapter>(sp => new ClaudeCodeAdapter(
    sp.GetRequiredService<ClaudeCliModelClient>(),
    catalog: sp.GetService<IModelCapabilityProvider>(),
    usageReporter: sp.GetService<LordHelm.Core.IUsageReporter>()));
builder.Services.AddSingleton<IAgentModelAdapter>(sp => new CodexCliAdapter(
    sp.GetRequiredService<CodexCliModelClient>(),
    catalog: sp.GetService<IModelCapabilityProvider>(),
    usageReporter: sp.GetService<LordHelm.Core.IUsageReporter>()));
builder.Services.AddSingleton<IAgentModelAdapter>(sp => new GeminiCliAdapter(
    sp.GetRequiredService<GeminiCliModelClient>(),
    catalog: sp.GetService<IModelCapabilityProvider>(),
    usageReporter: sp.GetService<LordHelm.Core.IUsageReporter>()));
builder.Services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
// Usage telemetry — real per-call accumulation + auth probes every 5 min.
// Panel-endorsed: no CLI has /status, so the only source of truth is
// adapter responses + auth-probe results from tiny inference calls.
builder.Services.AddSingleton<LordHelm.Core.UsageState>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.UsageAccumulator>();
builder.Services.AddSingleton<LordHelm.Core.IUsageReporter>(sp =>
    sp.GetRequiredService<LordHelm.Orchestrator.Usage.UsageAccumulator>());
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.AuthProbeSpecFactory>();
// Build one IUsageProbe per vendor, resolving the CURRENT Fast-tier model
// from IModelCatalog. ModelCatalogRefresher keeps that Fast resolution live
// so auth probes track whatever the CLI's subscription actually offers.
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.IUsageProbe>(sp => new LordHelm.Orchestrator.Usage.CliAuthProbe(
    sp.GetRequiredService<LordHelm.Orchestrator.Usage.AuthProbeSpecFactory>().BuildFor("claude"),
    sp.GetRequiredService<ILogger<LordHelm.Orchestrator.Usage.CliAuthProbe>>()));
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.IUsageProbe>(sp => new LordHelm.Orchestrator.Usage.CliAuthProbe(
    sp.GetRequiredService<LordHelm.Orchestrator.Usage.AuthProbeSpecFactory>().BuildFor("gemini"),
    sp.GetRequiredService<ILogger<LordHelm.Orchestrator.Usage.CliAuthProbe>>()));
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.IUsageProbe>(sp => new LordHelm.Orchestrator.Usage.CliAuthProbe(
    sp.GetRequiredService<LordHelm.Orchestrator.Usage.AuthProbeSpecFactory>().BuildFor("codex"),
    sp.GetRequiredService<ILogger<LordHelm.Orchestrator.Usage.CliAuthProbe>>()));
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.UsageProbeOptions>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Usage.UsageProbeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LordHelm.Orchestrator.Usage.UsageProbeService>());
builder.Services.AddHostedService<LordHelm.Orchestrator.Usage.SubscriptionExhaustionMonitor>();
builder.Services.AddSingleton<IRouterWeights, RouterWeightsProvider>();
builder.Services.AddSingleton<IRouterWeightsStore>(sp =>
{
    var path = Path.Combine(dataDir, "routing-weights.json");
    return new JsonFileRouterWeightsStore(path, sp.GetRequiredService<ILogger<JsonFileRouterWeightsStore>>());
});
builder.Services.AddHostedService<RouterWeightsPersistenceHostedService>();

// Primary-CLI preference — drives default vendor/model/tier for every dispatch
// surface (throne, chat, /helm commands). Persisted to data/helm-preference.json.
builder.Services.AddSingleton<HelmPreferenceState>();
builder.Services.AddSingleton<IHelmPreferenceStore>(sp =>
{
    var path = Path.Combine(dataDir, "helm-preference.json");
    return new JsonFileHelmPreferenceStore(path,
        sp.GetRequiredService<ILogger<JsonFileHelmPreferenceStore>>());
});
builder.Services.AddHostedService<HelmPreferencePersistenceHostedService>();

builder.Services.AddSingleton<IAdapterRouter, AdapterRouter>();
builder.Services.AddSingleton<AdapterProviderOrchestrator>();
builder.Services.AddSingleton<IProviderOrchestrator>(sp => sp.GetRequiredService<AdapterProviderOrchestrator>());
builder.Services.AddSingleton<IProviderHealth>(sp => sp.GetRequiredService<AdapterProviderOrchestrator>());

// ---------------------------------------------------------------- monitor
builder.Services.AddSingleton<Watcher>();
builder.Services.AddSingleton<IProcessMonitor>(sp => sp.GetRequiredService<Watcher>());
builder.Services.AddSingleton<SseLogBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SseLogBroadcaster>());

// ---------------------------------------------------------------- orchestrator
builder.Services.AddSingleton<LlmDecomposerOptions>();
builder.Services.AddSingleton<ModelCatalog>(_ => new ModelCatalog());
builder.Services.AddSingleton<IModelCatalog>(sp => sp.GetRequiredService<ModelCatalog>());
builder.Services.AddSingleton<IModelCapabilityProvider>(sp => sp.GetRequiredService<ModelCatalog>());
builder.Services.AddSingleton<IModelCatalogStore>(sp =>
{
    var path = Path.Combine(dataDir, "models.json");
    return new JsonFileModelCatalogStore(path, sp.GetRequiredService<ILogger<JsonFileModelCatalogStore>>());
});
builder.Services.AddHostedService<ModelCatalogPersistenceHostedService>();

// CLI-driven model discovery: each vendor's CLI is the authoritative source
// for its available models (a `/model` slash command or equivalent). Probe
// specs are mutable via /models/probes and persisted to data/model-probes.json.
// Each vendor's composed prober tries the native CLI first; on failure it
// falls back to asking the model itself to enumerate its siblings.
builder.Services.AddSingleton<IModelListParser, NumberedListModelParser>();
builder.Services.AddSingleton<IModelProbeRegistry>(_ => new ModelProbeRegistry());
builder.Services.AddSingleton<IModelProbeConfigStore>(sp =>
{
    var path = Path.Combine(dataDir, "model-probes.json");
    return new JsonFileModelProbeConfigStore(path, sp.GetRequiredService<ILogger<JsonFileModelProbeConfigStore>>());
});
builder.Services.AddHostedService<ModelProbeConfigPersistenceHostedService>();
foreach (var vendorId in new[] { "claude", "gemini", "codex" })
{
    builder.Services.AddSingleton<IModelProber>(sp => new FallbackCompositeProber(
        primary: new CliModelProber(
            vendorId,
            () => sp.GetRequiredService<IModelProbeRegistry>().Get(vendorId),
            sp.GetRequiredService<IModelListParser>(),
            sp.GetRequiredService<ILogger<CliModelProber>>()),
        fallback: new LlmFallbackProber(
            vendorId,
            sp.GetRequiredService<IProviderOrchestrator>(),
            sp.GetRequiredService<IModelListParser>(),
            sp.GetRequiredService<ILogger<LlmFallbackProber>>()),
        logger: sp.GetRequiredService<ILogger<FallbackCompositeProber>>()));
}
builder.Services.AddSingleton<ModelCatalogRefresherOptions>();
builder.Services.AddSingleton<ModelCatalogRefresher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelCatalogRefresher>());
// Event-driven trigger: re-probe a vendor's models whenever its auth state
// flips from failing to OK. Pairs with Scout's onMutation hook (which
// triggers on CLI flag/version changes) so the catalog reacts to real
// signals, not just the 6h timer.
builder.Services.AddHostedService<UsageStateModelRefreshTrigger>();
builder.Services.AddSingleton<IGoalDecomposer, LlmGoalDecomposer>();
builder.Services.AddSingleton<ExpertDirectory>(_ => ExpertDirectory.Default());
builder.Services.AddSingleton<IExpertRegistry, ExpertRegistry>();
builder.Services.AddSingleton<IExpertOverrideStore>(sp =>
{
    var path = Path.Combine(dataDir, "experts.json");
    return new JsonFileExpertOverrideStore(path, sp.GetRequiredService<ILogger<JsonFileExpertOverrideStore>>());
});
builder.Services.AddHostedService<ExpertOverridePersistenceHostedService>();
var useLlmAggregator = string.Equals(builder.Configuration["LORDHELM_LLM_SWARM"]
    ?? Environment.GetEnvironmentVariable("LORDHELM_LLM_SWARM"), "true", StringComparison.OrdinalIgnoreCase);
if (useLlmAggregator)
    builder.Services.AddSingleton<ISwarmAggregator, LlmSwarmAggregator>();
else
    builder.Services.AddSingleton<ISwarmAggregator, ConcatSwarmAggregator>();
builder.Services.AddSingleton<ISynthesizer, LlmSynthesizer>();
var useEngramDriven = string.Equals(builder.Configuration["LORDHELM_ENGRAM_DRIVEN"]
    ?? Environment.GetEnvironmentVariable("LORDHELM_ENGRAM_DRIVEN"), "true", StringComparison.OrdinalIgnoreCase);
if (useEngramDriven)
    builder.Services.AddSingleton<ILordHelmManager, EngramDrivenManager>();
else
    builder.Services.AddSingleton<ILordHelmManager, LordHelmManager>();
builder.Services.AddSingleton<IExpertProvisioner, DefaultExpertProvisioner>();
builder.Services.AddSingleton<DataflowBus>();
builder.Services.AddSingleton<IDataflowBus>(sp => sp.GetRequiredService<DataflowBus>());
builder.Services.AddSingleton<IGoalProgressSink, WidgetGoalProgressSink>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Consult.IConsultStrategy, LordHelm.Orchestrator.Consult.TieredConsultStrategy>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Consult.IPanelRunner, LordHelm.Orchestrator.Consult.PanelRunner>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Cortex.ILordHelmCortex, LordHelm.Orchestrator.Cortex.LordHelmCortex>();
builder.Services.AddHostedService<LordHelm.Orchestrator.Cortex.CortexAutoPromoteService>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Cortex.DailyReflectionOverseer>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Knowledge.KnowledgeOptions>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Knowledge.IKnowledgeService, LordHelm.Orchestrator.Knowledge.EngramKnowledgeService>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Chat.IChatRouter, LordHelm.Orchestrator.Chat.LlmChatRouter>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Chat.SafetyFloor>();
builder.Services.AddSingleton<LordHelm.Orchestrator.Chat.IChatDispatcher, LordHelm.Orchestrator.Chat.ChatDispatcher>();
builder.Services.AddSingleton<IGoalRunner, GoalRunner>();

// ---------------------------------------------------------------- overseer agents
builder.Services.AddSingleton<IAlertTray, InMemoryAlertTray>();
builder.Services.AddSingleton<OverseerRegistry>();
builder.Services.AddSingleton<OverseerRunnerOptions>();
builder.Services.AddSingleton<OverseerRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OverseerRunner>());
builder.Services.AddSingleton<DocumentCuratorAgent>(sp =>
{
    var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
    return new DocumentCuratorAgent(repoRoot, sp.GetRequiredService<ILogger<DocumentCuratorAgent>>());
});
builder.Services.AddHostedService<OverseerBootstrapHostedService>();

// ---------------------------------------------------------------- consensus
builder.Services.AddSingleton<INoveltyCheck, TokenOverlapNoveltyCheck>();
builder.Services.AddSingleton<DiagnosticPanelOptions>();
builder.Services.AddSingleton<IReadOnlyList<IPanelVoter>>(sp => new IPanelVoter[]
{
    new CliPanelVoter("claude", sp.GetRequiredService<ClaudeCliModelClient>(), "claude-opus-4-7",  sp.GetRequiredService<ILogger<CliPanelVoter>>()),
    new CliPanelVoter("gemini", sp.GetRequiredService<GeminiCliModelClient>(), "gemini-2.5-pro",   sp.GetRequiredService<ILogger<CliPanelVoter>>()),
    new CliPanelVoter("codex",  sp.GetRequiredService<CodexCliModelClient>(),  "gpt-5.4",          sp.GetRequiredService<ILogger<CliPanelVoter>>()),
});
builder.Services.AddSingleton<IConsensusProtocol, DiagnosticPanel>();

// ---------------------------------------------------------------- hosted services
builder.Services.AddHostedService<WatcherToWidgetBridge>();
builder.Services.AddHostedService<ApprovalQueueBridge>();
builder.Services.AddHostedService<IncidentResponder>();
builder.Services.AddHostedService<SkillStartupLoader>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHelmLogStream();
app.MapHelmGoalEndpoint();
app.MapArtifactEndpoint();

app.Run();

/// <summary>
/// Marker so <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// can target this top-level Program from the E2E test project.
/// </summary>
public partial class Program { }

/// <summary>
/// Registers every built-in <see cref="LordHelm.Orchestrator.Overseers.IOverseerAgent"/>
/// with the <see cref="LordHelm.Orchestrator.Overseers.OverseerRegistry"/> before the
/// runner starts sweeping. Runs once at startup.
/// </summary>
public sealed class OverseerBootstrapHostedService : IHostedService
{
    private readonly LordHelm.Orchestrator.Overseers.OverseerRegistry _registry;
    private readonly LordHelm.Orchestrator.Overseers.DocumentCuratorAgent _documentCurator;
    private readonly LordHelm.Orchestrator.Cortex.DailyReflectionOverseer _reflection;

    public OverseerBootstrapHostedService(
        LordHelm.Orchestrator.Overseers.OverseerRegistry registry,
        LordHelm.Orchestrator.Overseers.DocumentCuratorAgent documentCurator,
        LordHelm.Orchestrator.Cortex.DailyReflectionOverseer reflection)
    {
        _registry = registry;
        _documentCurator = documentCurator;
        _reflection = reflection;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _registry.Register(_documentCurator, enabledByDefault: true);
        _registry.Register(_reflection, enabledByDefault: true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// One-shot hosted service that runs on startup: scans the skills/ directory and
/// populates the SQLite cache + engram so the rest of the system has a skill library
/// to work against.
/// </summary>
public sealed class SkillStartupLoader : IHostedService
{
    private readonly ISkillLoader _loader;
    private readonly ISkillCache _cache;
    private readonly IEngramClient _engram;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SkillStartupLoader> _logger;

    public SkillStartupLoader(ISkillLoader loader, ISkillCache cache, IEngramClient engram, IWebHostEnvironment env, ILogger<SkillStartupLoader> logger)
    {
        _loader = loader;
        _cache = cache;
        _engram = engram;
        _env = env;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var skillsDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "skills"));
        if (!Directory.Exists(skillsDir))
        {
            _logger.LogWarning("skills directory not found: {Dir}", skillsDir);
            return;
        }
        // Seed upstream skill catalogues on first boot so a fresh install
        // gets a real library without manual XML authoring:
        // - claudedirectory.org/skills (30 reasoning skills)
        // - openai/skills .curated (40 deploy / figma / notion / security skills)
        // - google-gemini/gemini-skills (4 Gemini SDK skills)
        // Non-destructive: never overwrites an existing .skill.xml, so
        // operator edits survive the next startup.
        try
        {
            var claudeSeeded = await LordHelm.Skills.Seeding.ClaudeDirectorySkillSeed.EnsureSeededAsync(skillsDir, ct);
            if (claudeSeeded.Count > 0)
                _logger.LogInformation("Seeded {Count} claudedirectory skills", claudeSeeded.Count);
            var openaiSeeded = await LordHelm.Skills.Seeding.OpenAiCuratedSkillSeed.EnsureSeededAsync(skillsDir, ct);
            if (openaiSeeded.Count > 0)
                _logger.LogInformation("Seeded {Count} openai curated skills", openaiSeeded.Count);
            var geminiSeeded = await LordHelm.Skills.Seeding.GoogleGeminiSkillSeed.EnsureSeededAsync(skillsDir, ct);
            if (geminiSeeded.Count > 0)
                _logger.LogInformation("Seeded {Count} google-gemini skills", geminiSeeded.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "upstream skill seed failed; continuing with existing skills only");
        }

        // LoadDirectoryAsync parses every *.skill.xml once and stores them in
        // the SQLite cache. We then mirror to engram by iterating the cache
        // instead of re-parsing files from disk — one parse per manifest.
        var report = await _loader.LoadDirectoryAsync(skillsDir, ct);
        _logger.LogInformation("Skill loader: {New} new, {Skipped} unchanged, {Invalid} invalid of {Total}",
            report.Loaded, report.SkippedUnchanged, report.Invalid.Count, report.TotalFiles);

        var manifests = await _cache.ListAsync(ct);
        foreach (var manifest in manifests)
        {
            try
            {
                await _engram.StoreAsync(
                    "lord_helm_skills",
                    manifest.Id,
                    manifest.CanonicalXml,
                    category: "skill",
                    metadata: new Dictionary<string, string>
                    {
                        ["version"] = manifest.Version.ToString(),
                        ["hash"] = manifest.ContentHashSha256,
                        ["env"] = manifest.ExecEnv.ToString(),
                        ["risk"] = manifest.RiskTier.ToString(),
                    },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "engram mirror failed for skill {Id}", manifest.Id);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Opens the MCP engram stdio connection during startup. Runs after DI composition
/// so the rest of the system sees a connected client by the time any HostedService
/// tries to store/search.
/// </summary>
public sealed class McpEngramConnector : IHostedService
{
    private readonly McpEngramClient _client;
    private readonly ILogger<McpEngramConnector> _logger;
    public McpEngramConnector(McpEngramClient client, ILogger<McpEngramConnector> logger)
    {
        _client = client;
        _logger = logger;
    }
    // Fire-and-forget: don't block the hosted-service startup chain on the
    // MCP handshake (previously up to 30s). Downstream callers already gate
    // on `_connected` and return gracefully when the client is unavailable.
    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await _client.ConnectAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MCP engram connect failed; continuing in unavailable mode"); }
        }, ct);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
