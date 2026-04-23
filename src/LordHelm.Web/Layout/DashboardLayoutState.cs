using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Web.Layout;

public sealed record LayoutEntry(string Id, int Columns, int Rows);

/// <summary>
/// Live grid layout for the Home dashboard. Seeds with the design-handoff
/// default (topology + side widgets + fleet/active/kpis/gantt/queue/chat +
/// attention buckets). Mutated by drag-to-reorder in edit mode, by the LLM
/// layout-preset path, and by 'restore default'.
/// </summary>
public sealed class DashboardLayoutState
{
    private readonly object _gate = new();
    private List<LayoutEntry> _entries;
    private string? _presetKey;

    public event Action? OnChanged;

    public DashboardLayoutState()
    {
        _entries = DefaultEntries().ToList();
    }

    public IReadOnlyList<LayoutEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public string? CurrentPresetKey
    {
        get { lock (_gate) return _presetKey; }
    }

    public void SwapByIndex(int a, int b)
    {
        lock (_gate)
        {
            if (a < 0 || b < 0 || a >= _entries.Count || b >= _entries.Count || a == b) return;
            (_entries[a], _entries[b]) = (_entries[b], _entries[a]);
            _presetKey = null; // operator override
        }
        OnChanged?.Invoke();
    }

    public void Replace(IEnumerable<LayoutEntry> entries, string? presetKey = null)
    {
        lock (_gate)
        {
            _entries = entries.ToList();
            _presetKey = presetKey;
        }
        OnChanged?.Invoke();
    }

    public void RestoreDefault() => Replace(DefaultEntries(), presetKey: null);

    public static IEnumerable<LayoutEntry> DefaultEntries() => new[]
    {
        new LayoutEntry("topology",   9, 4),
        new LayoutEntry("health",     3, 2),
        new LayoutEntry("alerts",     3, 2),
        new LayoutEntry("approvals",  4, 2),
        new LayoutEntry("recent",     4, 2),
        new LayoutEntry("providers",  4, 2),
        new LayoutEntry("fleet",      3, 3),
        new LayoutEntry("active",     6, 3),
        new LayoutEntry("kpis",       3, 2),
        new LayoutEntry("gantt",      6, 2),
        new LayoutEntry("queue",      3, 2),
        new LayoutEntry("artifacts",  6, 3),
    };
}

public interface IDashboardLayoutStore
{
    Task LoadAsync(DashboardLayoutState state, CancellationToken ct = default);
    Task SaveAsync(DashboardLayoutState state, CancellationToken ct = default);
}

public sealed class JsonFileDashboardLayoutStore : IDashboardLayoutStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileDashboardLayoutStore> _logger;

    public JsonFileDashboardLayoutStore(string path, ILogger<JsonFileDashboardLayoutStore> logger)
    {
        _path = path; _logger = logger;
    }

    public async Task LoadAsync(DashboardLayoutState state, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var wire = await JsonSerializer.DeserializeAsync<Wire>(stream, JsonOpts, ct);
            if (wire?.Entries is null) return;
            state.Replace(
                wire.Entries.Select(e => new LayoutEntry(e.Id, e.Columns, e.Rows)),
                wire.PresetKey);
            _logger.LogInformation("Dashboard layout loaded from {Path}: {N} entries", _path, wire.Entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load dashboard layout from {Path}; keeping default.", _path);
        }
    }

    public async Task SaveAsync(DashboardLayoutState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var wire = new Wire
        {
            PresetKey = state.CurrentPresetKey,
            Entries = state.Entries.Select(e => new Entry(e.Id, e.Columns, e.Rows)).ToList(),
        };
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, wire, JsonOpts, ct);
        File.Move(tmp, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Wire
    {
        public string? PresetKey { get; set; }
        public List<Entry>? Entries { get; set; }
    }
    private sealed record Entry(string Id, int Columns, int Rows);
}

public sealed class DashboardLayoutPersistenceHostedService : IHostedService, IAsyncDisposable
{
    private readonly DashboardLayoutState _state;
    private readonly IDashboardLayoutStore _store;
    private readonly ILogger<DashboardLayoutPersistenceHostedService> _logger;
    private CancellationTokenSource? _debounceCts;

    public DashboardLayoutPersistenceHostedService(
        DashboardLayoutState state,
        IDashboardLayoutStore store,
        ILogger<DashboardLayoutPersistenceHostedService> logger)
    {
        _state = state; _store = store; _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(_state, ct);
        _state.OnChanged += Handle;
    }

    private void Handle()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, token); await _store.SaveAsync(_state, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Dashboard layout save failed"); }
        });
    }

    public Task StopAsync(CancellationToken ct) { _state.OnChanged -= Handle; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _debounceCts?.Cancel(); _debounceCts?.Dispose(); return ValueTask.CompletedTask; }
}
