namespace LordHelm.Orchestrator;

public enum ModelTier { Fast, Deep, Code }

public sealed record ModelEntry(
    string VendorId,
    string ModelId,
    ModelTier Tier,
    string Description,
    bool IsAvailable,
    DateTimeOffset LastProbed);

public sealed record McpToolEntry(
    string ServerName,
    string ToolName,
    string Description);

/// <summary>
/// Available-model registry spanning every vendor the orchestrator knows about plus
/// any MCP tool registrations inherited from shared integrations. The decomposer
/// and operators request models by <see cref="ModelTier"/> so business logic stays
/// vendor-agnostic; <see cref="Resolve"/> picks the best concrete <see cref="ModelId"/>
/// available at invocation time.
///
/// Entries are mutable from within Lord Helm: operators can add, edit, and remove
/// models through the <c>/models/new</c> page. A <see cref="OnChanged"/> event fires
/// on every mutation so the UI can re-render without polling, and a persistence layer
/// (<see cref="IModelCatalogStore"/>) serialises the catalog to disk so changes
/// survive a restart.
/// </summary>
public interface IModelCatalog
{
    event Action? OnChanged;

    IReadOnlyList<ModelEntry> GetModels(string? vendorId = null);

    /// <summary>Pick the best available model for a tier, optionally biased by vendor.</summary>
    ModelEntry? Resolve(ModelTier tier, string? preferredVendor = null);

    IReadOnlyList<McpToolEntry> GetMcpTools(string? serverName = null);

    IReadOnlyList<string> Vendors();

    void Upsert(ModelEntry entry);
    bool Remove(string vendorId, string modelId);
    void MarkAvailability(string vendorId, string modelId, bool available);
    void RegisterMcpTool(McpToolEntry tool);
    bool RemoveMcpTool(string serverName, string toolName);

    /// <summary>Replace the full catalog (used by the persistence loader at startup).</summary>
    void ReplaceAll(IEnumerable<ModelEntry> entries, IEnumerable<McpToolEntry> tools);
}

public sealed class ModelCatalog : IModelCatalog
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string vendor, string model), ModelEntry> _models;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string server, string tool), McpToolEntry> _tools;

    public event Action? OnChanged;

    public ModelCatalog(IEnumerable<ModelEntry>? seed = null)
    {
        _models = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), ModelEntry>(
            (seed ?? DefaultSeed()).ToDictionary(m => (m.VendorId, m.ModelId), m => m));
        _tools = new();
    }

    public IReadOnlyList<ModelEntry> GetModels(string? vendorId = null)
    {
        var q = _models.Values.AsEnumerable();
        if (vendorId is not null)
            q = q.Where(m => string.Equals(m.VendorId, vendorId, StringComparison.OrdinalIgnoreCase));
        return q.OrderBy(m => m.VendorId).ThenBy(m => m.Tier).ThenBy(m => m.ModelId).ToList();
    }

    public ModelEntry? Resolve(ModelTier tier, string? preferredVendor = null)
    {
        IEnumerable<ModelEntry> candidates = _models.Values.Where(m => m.IsAvailable && m.Tier == tier);
        if (preferredVendor is not null)
        {
            var biased = candidates
                .OrderBy(m => string.Equals(m.VendorId, preferredVendor, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.ModelId);
            return biased.FirstOrDefault();
        }
        return candidates.OrderBy(m => m.ModelId).FirstOrDefault();
    }

    public IReadOnlyList<McpToolEntry> GetMcpTools(string? serverName = null) =>
        (serverName is null
            ? _tools.Values
            : _tools.Values.Where(t => string.Equals(t.ServerName, serverName, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(t => t.ServerName).ThenBy(t => t.ToolName)
        .ToList();

    public IReadOnlyList<string> Vendors() =>
        _models.Values.Select(m => m.VendorId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

    public void Upsert(ModelEntry entry)
    {
        _models[(entry.VendorId, entry.ModelId)] = entry;
        OnChanged?.Invoke();
    }

    public bool Remove(string vendorId, string modelId)
    {
        var removed = _models.TryRemove((vendorId, modelId), out _);
        if (removed) OnChanged?.Invoke();
        return removed;
    }

    public void MarkAvailability(string vendorId, string modelId, bool available)
    {
        var key = (vendorId, modelId);
        _models.AddOrUpdate(key,
            _ => new ModelEntry(vendorId, modelId, ModelTier.Deep, "", available, DateTimeOffset.UtcNow),
            (_, existing) => existing with { IsAvailable = available, LastProbed = DateTimeOffset.UtcNow });
        OnChanged?.Invoke();
    }

    public void RegisterMcpTool(McpToolEntry tool)
    {
        _tools[(tool.ServerName, tool.ToolName)] = tool;
        OnChanged?.Invoke();
    }

    public bool RemoveMcpTool(string serverName, string toolName)
    {
        var removed = _tools.TryRemove((serverName, toolName), out _);
        if (removed) OnChanged?.Invoke();
        return removed;
    }

    public void ReplaceAll(IEnumerable<ModelEntry> entries, IEnumerable<McpToolEntry> tools)
    {
        _models.Clear();
        foreach (var e in entries) _models[(e.VendorId, e.ModelId)] = e;
        _tools.Clear();
        foreach (var t in tools) _tools[(t.ServerName, t.ToolName)] = t;
        OnChanged?.Invoke();
    }

    public static IReadOnlyList<ModelEntry> DefaultSeed()
    {
        var now = DateTimeOffset.UtcNow;
        return new[]
        {
            // claude
            new ModelEntry("claude", "claude-opus-4-7",   ModelTier.Deep, "Anthropic Opus — deepest reasoning, highest cost",   true, now),
            new ModelEntry("claude", "claude-sonnet-4-6", ModelTier.Deep, "Anthropic Sonnet — balanced reasoning + throughput", true, now),
            new ModelEntry("claude", "claude-haiku-4-5",  ModelTier.Fast, "Anthropic Haiku — fast + cheap, 1M ctx variant",     true, now),
            // gemini
            new ModelEntry("gemini", "gemini-2.5-pro",    ModelTier.Deep, "Google Gemini 2.5 Pro — deep reasoning",             true, now),
            new ModelEntry("gemini", "gemini-2.5-flash",  ModelTier.Fast, "Google Gemini 2.5 Flash — fast tier",                true, now),
            // codex / openai-compat
            new ModelEntry("codex",  "o4",                ModelTier.Deep, "OpenAI o4 — reasoning",                              true, now),
            new ModelEntry("codex",  "gpt-5-codex",       ModelTier.Code, "OpenAI codex — code generation tier",                true, now),
        };
    }
}
