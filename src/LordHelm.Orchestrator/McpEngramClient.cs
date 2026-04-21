using System.Text.Json;
using LordHelm.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LordHelm.Orchestrator;

public sealed record McpEngramOptions
{
    /// <summary>Executable that launches the mcp-engram-memory server (usually `node` or `dotnet`).</summary>
    public string Command { get; init; } = "dotnet";
    /// <summary>Arguments passed to the server. Default assumes local sibling repo.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = new[]
    {
        "run", "--project",
        "../mcps/mcp-engram-memory/src/McpEngramMemory.Server/McpEngramMemory.Server.csproj",
        "--no-build",
    };
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Live MCP-wired <see cref="IEngramClient"/>. Launches the mcp-engram-memory
/// server as a child process over stdio and invokes its tools via the official
/// <c>ModelContextProtocol</c> client SDK. Call <see cref="ConnectAsync"/> once
/// at startup; after that <see cref="StoreAsync"/> / <see cref="SearchAsync"/> /
/// <see cref="GetAsync"/> proxy straight through.
///
/// On connection failure this client stays in an "unavailable" state and every
/// call returns gracefully (no throw). Program.cs chooses between this and the
/// in-process <see cref="EngramClient"/> via <see cref="IsAvailableAsync"/>.
/// </summary>
public sealed class McpEngramClient : IEngramClient, IAsyncDisposable
{
    private readonly McpEngramOptions _options;
    private readonly ILogger<McpEngramClient> _logger;
    private McpClient? _client;
    private bool _connected;

    public McpEngramClient(McpEngramOptions options, ILogger<McpEngramClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "mcp-engram-memory",
                Command = _options.Command,
                Arguments = _options.Arguments.ToArray(),
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.InitializeTimeout);

            _client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);
            _connected = true;
            _logger.LogInformation("MCP engram client connected to {Command}", _options.Command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect to mcp-engram-memory; falling back to in-process client.");
            _connected = false;
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(_connected);

    public async Task StoreAsync(string @namespace, string id, string text, string? category = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        if (!_connected || _client is null) return;

        var args = new Dictionary<string, object?>
        {
            ["ns"] = @namespace,
            ["id"] = id,
            ["text"] = text,
        };
        if (category is not null) args["category"] = category;
        if (metadata is not null && metadata.Count > 0)
            args["metadata"] = metadata.ToDictionary(k => k.Key, k => (object?)k.Value);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.CallTimeout);
            await _client.CallToolAsync("store_memory", args, cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "engram.store via MCP failed ns={Ns} id={Id}", @namespace, id);
        }
    }

    public async Task<IReadOnlyList<EngramHit>> SearchAsync(string @namespace, string text, int k = 5, CancellationToken ct = default)
    {
        if (!_connected || _client is null) return Array.Empty<EngramHit>();

        var args = new Dictionary<string, object?>
        {
            ["ns"] = @namespace,
            ["text"] = text,
            ["k"] = k,
            ["hybrid"] = true,
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.CallTimeout);
            var result = await _client.CallToolAsync("search_memory", args, cancellationToken: cts.Token);
            return ParseHits(result, @namespace);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "engram.search via MCP failed ns={Ns}", @namespace);
            return Array.Empty<EngramHit>();
        }
    }

    public async Task<EngramHit?> GetAsync(string @namespace, string id, CancellationToken ct = default)
    {
        if (!_connected || _client is null) return null;

        var args = new Dictionary<string, object?>
        {
            ["ns"] = @namespace,
            ["id"] = id,
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.CallTimeout);
            var result = await _client.CallToolAsync("get_memory", args, cancellationToken: cts.Token);
            var hits = ParseHits(result, @namespace);
            return hits.Count > 0 ? hits[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "engram.get via MCP failed ns={Ns} id={Id}", @namespace, id);
            return null;
        }
    }

    private static IReadOnlyList<EngramHit> ParseHits(CallToolResult result, string fallbackNamespace)
    {
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock text) continue;
            try
            {
                using var doc = JsonDocument.Parse(text.Text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    return arr.EnumerateArray().Select(e => ToHit(e, fallbackNamespace)).ToList();
                }
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return root.EnumerateArray().Select(e => ToHit(e, fallbackNamespace)).ToList();
                }
                if (root.ValueKind == JsonValueKind.Object)
                {
                    return new[] { ToHit(root, fallbackNamespace) };
                }
            }
            catch (JsonException) { }
        }
        return Array.Empty<EngramHit>();
    }

    private static EngramHit ToHit(JsonElement e, string fallbackNamespace)
    {
        string Ns() => e.TryGetProperty("ns", out var n) ? n.GetString() ?? fallbackNamespace : fallbackNamespace;
        string Id() => e.TryGetProperty("id", out var n) ? n.GetString() ?? "" : "";
        string Text() => e.TryGetProperty("text", out var n) ? n.GetString() ?? "" : "";
        double Score() => e.TryGetProperty("score", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0.0;
        var meta = new Dictionary<string, string>();
        if (e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in m.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) meta[p.Name] = p.Value.GetString() ?? "";
        }
        return new EngramHit(Ns(), Id(), Text(), Score(), meta);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable ad)
        {
            try { await ad.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "MCP client dispose failed"); }
        }
        _client = null;
        _connected = false;
    }
}
