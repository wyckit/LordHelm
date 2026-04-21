using LordHelm.Execution;

namespace LordHelm.Execution.Tests;

public class AuditLogTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");

    public void Dispose() { try { File.Delete(_db); } catch { } }

    [Fact]
    public async Task Appends_Are_Chained()
    {
        var log = new SqliteAuditLog(_db);
        await log.InitializeAsync();

        var a = await log.AppendAsync("s1", "Write", "Approved", "op1", "sess1", null);
        var b = await log.AppendAsync("s2", "Delete", "Denied", "op1", "sess1", "user cancelled");

        Assert.Equal(new string('0', 64), a.PrevHashHex);
        Assert.Equal(a.EntryHashHex, b.PrevHashHex);
        Assert.True(await log.VerifyChainAsync());
    }

    [Fact]
    public async Task Empty_Chain_Verifies()
    {
        var log = new SqliteAuditLog(_db);
        await log.InitializeAsync();
        Assert.True(await log.VerifyChainAsync());
    }
}
