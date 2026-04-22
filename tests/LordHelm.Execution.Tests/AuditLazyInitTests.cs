using LordHelm.Execution;

namespace LordHelm.Execution.Tests;

/// <summary>
/// Regression: production was hitting `SQLite Error 1: 'no such table: audit'`
/// because <see cref="SqliteAuditLog.AppendAsync"/> was called by the approval
/// gate before any caller had invoked <c>InitializeAsync</c>. Every public
/// method should now lazy-init the schema exactly once.
/// </summary>
public class AuditLazyInitTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"audit-lazy-{Guid.NewGuid():N}.db");
    public void Dispose() { try { File.Delete(_db); } catch { } }

    [Fact]
    public async Task AppendAsync_Before_Explicit_Initialize_Does_Not_Throw()
    {
        var log = new SqliteAuditLog(_db);
        // Intentionally no InitializeAsync call here — mirrors the production
        // path where the approval gate appended before any startup hook.
        var entry = await log.AppendAsync("read-file", "Write", "Approved", "op", "sess", null);
        Assert.Equal(new string('0', 64), entry.PrevHashHex);
        Assert.True(await log.VerifyChainAsync());
    }

    [Fact]
    public async Task Concurrent_AppendAsync_Creates_Table_Exactly_Once()
    {
        var log = new SqliteAuditLog(_db);
        var tasks = Enumerable.Range(0, 16)
            .Select(i => log.AppendAsync($"skill-{i}", "Write", "Approved", "op", "sess", null))
            .ToArray();
        var entries = await Task.WhenAll(tasks);
        Assert.Equal(16, entries.Length);
        Assert.Equal(16, entries.Select(e => e.EntryHashHex).Distinct().Count());
        Assert.True(await log.VerifyChainAsync());
    }

    [Fact]
    public async Task RecentAsync_Before_Initialize_Returns_Empty()
    {
        var log = new SqliteAuditLog(_db);
        var recent = await log.RecentAsync(100);
        Assert.Empty(recent);
    }
}
