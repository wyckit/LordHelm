using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

public interface IModelCatalogStore
{
    Task LoadAsync(IModelCatalog catalog, CancellationToken ct = default);
    Task SaveAsync(IModelCatalog catalog, CancellationToken ct = default);
}

/// <summary>
/// JSON-file persistence for <see cref="IModelCatalog"/>. On startup, loads the
/// file (if present) and <c>ReplaceAll</c>s the catalog; otherwise keeps the
/// default seed. Auto-saves on every <c>OnChanged</c> event so operator edits
/// survive a restart.
/// </summary>
public sealed class JsonFileModelCatalogStore : IModelCatalogStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileModelCatalogStore> _logger;

    public JsonFileModelCatalogStore(string path, ILogger<JsonFileModelCatalogStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task LoadAsync(IModelCatalog catalog, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var wire = await JsonSerializer.DeserializeAsync<Wire>(stream, JsonOpts, ct);
            if (wire is null) return;
            catalog.ReplaceAll(
                wire.Models ?? new List<ModelEntry>(),
                wire.McpTools ?? new List<McpToolEntry>());
            _logger.LogInformation("Model catalog loaded from {Path}: {Models} models, {Tools} MCP tools",
                _path, wire.Models?.Count ?? 0, wire.McpTools?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load model catalog from {Path}; keeping default seed.", _path);
        }
    }

    public async Task SaveAsync(IModelCatalog catalog, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var wire = new Wire
        {
            Models = catalog.GetModels().ToList(),
            McpTools = catalog.GetMcpTools().ToList(),
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
        public List<ModelEntry>? Models { get; set; }
        public List<McpToolEntry>? McpTools { get; set; }
    }
}

/// <summary>
/// Drives <see cref="IModelCatalogStore"/>: loads on startup, subscribes to
/// <see cref="IModelCatalog.OnChanged"/>, and debounces saves so rapid edits
/// don't spam the disk. Registered as a HostedService.
/// </summary>
public sealed class ModelCatalogPersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly IModelCatalog _catalog;
    private readonly IModelCatalogStore _store;
    private readonly ILogger<ModelCatalogPersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public ModelCatalogPersistenceHostedService(
        IModelCatalog catalog,
        IModelCatalogStore store,
        ILogger<ModelCatalogPersistenceHostedService> logger)
    {
        _catalog = catalog;
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(_catalog, ct);
        _catalog.OnChanged += HandleChanged;
    }

    private void HandleChanged()
    {
        // 500 ms debounce — a chain of Upserts from a single UI form submit
        // collapses into one disk write.
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await _store.SaveAsync(_catalog, CancellationToken.None);
            }
            catch (OperationCanceledException) { /* superseded by a newer change */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ModelCatalog save failed");
            }
        });
    }

    public Task StopAsync(CancellationToken ct)
    {
        _catalog.OnChanged -= HandleChanged;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
