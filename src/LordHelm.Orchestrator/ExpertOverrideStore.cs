using System.Text.Json;
using System.Text.Json.Serialization;
using LordHelm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public interface IExpertOverrideStore
{
    Task LoadAsync(IExpertRegistry registry, CancellationToken ct = default);
    Task SaveAsync(IExpertRegistry registry, CancellationToken ct = default);
}

/// <summary>
/// JSON-file persistence for <see cref="IExpertRegistry"/> policy/budget
/// overrides. Mirrors <c>JsonFileModelCatalogStore</c>: atomic write via
/// <c>*.tmp + File.Move</c>; safe on missing file; camelCase + string enums
/// for human-readable edits.
/// </summary>
public sealed class JsonFileExpertOverrideStore : IExpertOverrideStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileExpertOverrideStore> _logger;

    public JsonFileExpertOverrideStore(string path, ILogger<JsonFileExpertOverrideStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task LoadAsync(IExpertRegistry registry, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var wire = await JsonSerializer.DeserializeAsync<Wire>(stream, JsonOpts, ct);
            if (wire?.Overrides is null) return;
            var dict = wire.Overrides.ToDictionary(
                e => e.Id,
                e => (new ExpertPolicy(e.PreferredMode, e.RequiresApproval, e.PinnedVendor, e.AlwaysDebate, e.PinnedModel, e.ThinkingEnabled),
                      new ExpertBudget(e.MaxTokensPerCall, e.MaxTokensPerGoal, e.MaxUsdPerGoal)),
                StringComparer.OrdinalIgnoreCase);
            registry.ReplaceAll(dict);
            _logger.LogInformation("Expert overrides loaded from {Path}: {Count} entries", _path, dict.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load expert overrides from {Path}; keeping defaults.", _path);
        }
    }

    public async Task SaveAsync(IExpertRegistry registry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var wire = new Wire
        {
            Overrides = registry.GetOverrides()
                .Select(kv => new Entry(
                    Id: kv.Key,
                    PreferredMode: kv.Value.Policy.PreferredMode,
                    RequiresApproval: kv.Value.Policy.RequiresApproval,
                    PinnedVendor: kv.Value.Policy.PinnedVendor,
                    MaxTokensPerCall: kv.Value.Budget.MaxTokensPerCall,
                    MaxTokensPerGoal: kv.Value.Budget.MaxTokensPerGoal,
                    MaxUsdPerGoal: kv.Value.Budget.MaxUsdPerGoal,
                    AlwaysDebate: kv.Value.Policy.AlwaysDebate,
                    PinnedModel: kv.Value.Policy.PinnedModel,
                    ThinkingEnabled: kv.Value.Policy.ThinkingEnabled))
                .ToList(),
        };
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, wire, JsonOpts, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Wire
    {
        public List<Entry>? Overrides { get; set; }
    }

    private sealed record Entry(
        string Id,
        ResourceMode PreferredMode,
        bool RequiresApproval,
        string? PinnedVendor,
        int MaxTokensPerCall,
        int MaxTokensPerGoal,
        decimal MaxUsdPerGoal,
        bool AlwaysDebate = false,
        string? PinnedModel = null,
        bool ThinkingEnabled = false);
}

/// <summary>
/// Loads on startup, saves on every <see cref="IExpertRegistry.OnChanged"/>
/// with 500 ms debounce — identical pattern to
/// <see cref="ModelCatalogPersistenceHostedService"/>.
/// </summary>
public sealed class ExpertOverridePersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly IExpertRegistry _registry;
    private readonly IExpertOverrideStore _store;
    private readonly ILogger<ExpertOverridePersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public ExpertOverridePersistenceHostedService(
        IExpertRegistry registry,
        IExpertOverrideStore store,
        ILogger<ExpertOverridePersistenceHostedService> logger)
    {
        _registry = registry;
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(_registry, ct);
        _registry.OnChanged += HandleChanged;
    }

    private void HandleChanged()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await _store.SaveAsync(_registry, CancellationToken.None);
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Expert override save failed");
            }
        });
    }

    public Task StopAsync(CancellationToken ct)
    {
        _registry.OnChanged -= HandleChanged;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
