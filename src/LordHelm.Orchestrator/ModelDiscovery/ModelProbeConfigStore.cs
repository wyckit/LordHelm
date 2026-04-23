using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator.ModelDiscovery;

public interface IModelProbeConfigStore
{
    Task LoadAsync(IModelProbeRegistry registry, CancellationToken ct = default);
    Task SaveAsync(IModelProbeRegistry registry, CancellationToken ct = default);
}

public sealed class JsonFileModelProbeConfigStore : IModelProbeConfigStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileModelProbeConfigStore> _logger;

    public JsonFileModelProbeConfigStore(string path, ILogger<JsonFileModelProbeConfigStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task LoadAsync(IModelProbeRegistry registry, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var wire = await JsonSerializer.DeserializeAsync<Wire>(stream, JsonOpts, ct);
            if (wire?.Specs is null) return;
            registry.ReplaceAll(wire.Specs.Select(s => new ModelProbeSpec(
                VendorId: s.VendorId,
                Executable: s.Executable,
                Args: (IReadOnlyList<string>?)s.Args ?? Array.Empty<string>(),
                StdinInput: s.StdinInput,
                Timeout: s.TimeoutSeconds is null ? null : TimeSpan.FromSeconds(s.TimeoutSeconds.Value))));
            _logger.LogInformation("Probe specs loaded from {Path}: {Count} vendors", _path, wire.Specs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load probe specs from {Path}; keeping defaults.", _path);
        }
    }

    public async Task SaveAsync(IModelProbeRegistry registry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var wire = new Wire
        {
            Specs = registry.All.Select(s => new Entry(
                VendorId: s.VendorId,
                Executable: s.Executable,
                Args: s.Args.ToList(),
                StdinInput: s.StdinInput,
                TimeoutSeconds: (int?)s.Timeout?.TotalSeconds)).ToList(),
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
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Wire { public List<Entry>? Specs { get; set; } }
    private sealed record Entry(
        string VendorId,
        string Executable,
        List<string>? Args,
        string? StdinInput,
        int? TimeoutSeconds);
}

public sealed class ModelProbeConfigPersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly IModelProbeRegistry _registry;
    private readonly IModelProbeConfigStore _store;
    private readonly ILogger<ModelProbeConfigPersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public ModelProbeConfigPersistenceHostedService(
        IModelProbeRegistry registry,
        IModelProbeConfigStore store,
        ILogger<ModelProbeConfigPersistenceHostedService> logger)
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
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Probe spec save failed"); }
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
