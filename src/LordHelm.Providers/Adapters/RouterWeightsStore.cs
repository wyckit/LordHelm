using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Providers.Adapters;

public interface IRouterWeightsStore
{
    Task LoadAsync(IRouterWeights weights, CancellationToken ct = default);
    Task SaveAsync(IRouterWeights weights, CancellationToken ct = default);
}

/// <summary>
/// JSON-file persistence for <see cref="IRouterWeights"/>. Mirrors
/// <c>JsonFileModelCatalogStore</c>: safe on missing file, atomic
/// <c>*.tmp + File.Move</c> on save, camelCase indented output.
/// </summary>
public sealed class JsonFileRouterWeightsStore : IRouterWeightsStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileRouterWeightsStore> _logger;

    public JsonFileRouterWeightsStore(string path, ILogger<JsonFileRouterWeightsStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task LoadAsync(IRouterWeights weights, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<RouterWeights>(stream, JsonOpts, ct);
            if (loaded is null) return;
            weights.Replace(loaded);
            _logger.LogInformation("Router weights loaded from {Path}", _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load router weights from {Path}; keeping defaults.", _path);
        }
    }

    public async Task SaveAsync(IRouterWeights weights, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, weights.Current, JsonOpts, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public sealed class RouterWeightsPersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly IRouterWeights _weights;
    private readonly IRouterWeightsStore _store;
    private readonly ILogger<RouterWeightsPersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public RouterWeightsPersistenceHostedService(
        IRouterWeights weights,
        IRouterWeightsStore store,
        ILogger<RouterWeightsPersistenceHostedService> logger)
    {
        _weights = weights;
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(_weights, ct);
        _weights.OnChanged += HandleChanged;
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
                await _store.SaveAsync(_weights, CancellationToken.None);
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Router weights save failed"); }
        });
    }

    public Task StopAsync(CancellationToken ct)
    {
        _weights.OnChanged -= HandleChanged;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
