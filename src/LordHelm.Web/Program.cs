using LordHelm.Consensus;
using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Monitor;
using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Overseers;
using LordHelm.Providers;
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
    onMutation: ev => sp.GetRequiredService<ITranspilerCacheInvalidator>().Invalidate(ev.VendorId, ev.ToVersion)));
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
builder.Services.AddSingleton<IExecutionRouter, ExecutionRouter>();

// ---------------------------------------------------------------- providers
builder.Services.AddSingleton<ClaudeCliModelClient>();
builder.Services.AddSingleton<GeminiCliModelClient>();
builder.Services.AddSingleton<CodexCliModelClient>();
builder.Services.AddSingleton<MultiProviderOrchestrator>(sp => new MultiProviderOrchestrator(
    providers: new[]
    {
        new ProviderConfig("claude", "claude-opus-4-7", new RateLimitGovernor(60, TimeSpan.FromMinutes(1)), sp.GetRequiredService<ClaudeCliModelClient>(), Priority: 100),
        new ProviderConfig("gemini", "gemini-2.5-pro",  new RateLimitGovernor(60, TimeSpan.FromMinutes(1)), sp.GetRequiredService<GeminiCliModelClient>(), Priority: 80),
        new ProviderConfig("codex",  "o4",              new RateLimitGovernor(60, TimeSpan.FromMinutes(1)), sp.GetRequiredService<CodexCliModelClient>(),  Priority: 60),
    },
    policy: FailoverPolicy.PriorityWeighted,
    logger: sp.GetRequiredService<ILogger<MultiProviderOrchestrator>>()));
builder.Services.AddSingleton<IProviderOrchestrator>(sp => sp.GetRequiredService<MultiProviderOrchestrator>());
builder.Services.AddSingleton<IProviderHealth>(sp => sp.GetRequiredService<MultiProviderOrchestrator>());

// ---------------------------------------------------------------- monitor
builder.Services.AddSingleton<Watcher>();
builder.Services.AddSingleton<IProcessMonitor>(sp => sp.GetRequiredService<Watcher>());
builder.Services.AddSingleton<SseLogBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SseLogBroadcaster>());

// ---------------------------------------------------------------- orchestrator
builder.Services.AddSingleton<LlmDecomposerOptions>();
builder.Services.AddSingleton<IModelCatalog>(_ => new ModelCatalog());
builder.Services.AddSingleton<IModelCatalogStore>(sp =>
{
    var path = Path.Combine(dataDir, "models.json");
    return new JsonFileModelCatalogStore(path, sp.GetRequiredService<ILogger<JsonFileModelCatalogStore>>());
});
builder.Services.AddHostedService<ModelCatalogPersistenceHostedService>();
builder.Services.AddSingleton<IGoalDecomposer, LlmGoalDecomposer>();
builder.Services.AddSingleton<ExpertDirectory>(_ => ExpertDirectory.Default());
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
    new CliPanelVoter("codex",  sp.GetRequiredService<CodexCliModelClient>(),  "o4",               sp.GetRequiredService<ILogger<CliPanelVoter>>()),
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

    public OverseerBootstrapHostedService(
        LordHelm.Orchestrator.Overseers.OverseerRegistry registry,
        LordHelm.Orchestrator.Overseers.DocumentCuratorAgent documentCurator)
    {
        _registry = registry;
        _documentCurator = documentCurator;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _registry.Register(_documentCurator, enabledByDefault: true);
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
    private readonly IEngramClient _engram;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SkillStartupLoader> _logger;

    public SkillStartupLoader(ISkillLoader loader, IEngramClient engram, IWebHostEnvironment env, ILogger<SkillStartupLoader> logger)
    {
        _loader = loader;
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
        var report = await _loader.LoadDirectoryAsync(skillsDir, ct);
        _logger.LogInformation("Skill loader: {New} new, {Skipped} unchanged, {Invalid} invalid of {Total}",
            report.Loaded, report.SkippedUnchanged, report.Invalid.Count, report.TotalFiles);

        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.skill.xml"))
        {
            try
            {
                var manifest = await _loader.LoadFileAsync(file, ct);
                if (manifest is null) continue;
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
                _logger.LogWarning(ex, "engram mirror failed for {File}", file);
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
    public McpEngramConnector(McpEngramClient client) { _client = client; }
    public Task StartAsync(CancellationToken ct) => _client.ConnectAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
