using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Orchestrator;

/// <summary>
/// Persistent preferences for Lord Helm's primary CLI + model. Drives the
/// default SelectedVendor/SelectedTier in the orchestration panel's throne
/// and every dispatch surface that doesn't override. Chat command
/// <c>/helm use {vendor} [{model}]</c> and the throne selects both mutate
/// this singleton; updates fire <see cref="OnChanged"/> and persist to
/// <c>data/helm-preference.json</c> via the debounced hosted service.
/// </summary>
/// <summary>
/// Primary CLI preference. <see cref="PrimaryVendor"/> defaults to empty —
/// a fresh install should wait for the operator to pick before dispatching
/// any goals. The orchestration panel renders "auto" when vendor is empty
/// and every dispatch surface falls through to the chat router's
/// persona-and-tier selection.
/// </summary>
public sealed record HelmPreference(
    string PrimaryVendor = "",
    string? PrimaryModel = null,
    string PrimaryTier = "Deep",
    // Operator toggle for extended-thinking / reasoning mode on models that
    // support it (claude opus/sonnet, gemini 2.5-pro / 3.1-pro, codex-max/-mini
    // reasoning, etc). Backend adapters pass this through to the CLI's
    // reasoning-effort flag. Default off for cheaper dispatches.
    bool ThinkingEnabled = false);

public sealed class HelmPreferenceState
{
    private readonly object _gate = new();
    private HelmPreference _current = new();

    public event Action? OnChanged;

    public HelmPreference Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(HelmPreference pref)
    {
        lock (_gate) _current = pref;
        OnChanged?.Invoke();
    }

    public void SetPrimary(string vendor, string? model = null, string? tier = null, bool? thinking = null)
    {
        lock (_gate)
        {
            _current = _current with
            {
                PrimaryVendor = vendor,
                PrimaryModel = model ?? _current.PrimaryModel,
                PrimaryTier = tier ?? _current.PrimaryTier,
                ThinkingEnabled = thinking ?? _current.ThinkingEnabled,
            };
        }
        OnChanged?.Invoke();
    }
}

public interface IHelmPreferenceStore
{
    Task LoadAsync(HelmPreferenceState state, CancellationToken ct = default);
    Task SaveAsync(HelmPreferenceState state, CancellationToken ct = default);
}

public sealed class JsonFileHelmPreferenceStore : IHelmPreferenceStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileHelmPreferenceStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonFileHelmPreferenceStore(string path, ILogger<JsonFileHelmPreferenceStore> logger)
    {
        _path = path; _logger = logger;
    }

    public async Task LoadAsync(HelmPreferenceState state, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<HelmPreference>(stream, JsonOpts, ct);
            if (loaded is not null) state.Set(loaded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load helm preference from {Path}; keeping defaults", _path);
        }
    }

    public async Task SaveAsync(HelmPreferenceState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, state.Current, JsonOpts, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}

public sealed class HelmPreferencePersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly HelmPreferenceState _state;
    private readonly IHelmPreferenceStore _store;
    private readonly ILogger<HelmPreferencePersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public HelmPreferencePersistenceHostedService(
        HelmPreferenceState state,
        IHelmPreferenceStore store,
        ILogger<HelmPreferencePersistenceHostedService> logger)
    {
        _state = state; _store = store; _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(_state, ct);
        _state.OnChanged += HandleChanged;
    }

    private void HandleChanged()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, token); await _store.SaveAsync(_state, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "helm-preference save failed"); }
        });
    }

    public Task StopAsync(CancellationToken ct) { _state.OnChanged -= HandleChanged; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _debounceCts?.Cancel(); _debounceCts?.Dispose(); return ValueTask.CompletedTask; }
}
