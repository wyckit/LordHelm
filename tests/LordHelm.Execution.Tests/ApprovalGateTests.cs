using LordHelm.Core;
using LordHelm.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Execution.Tests;

public class ApprovalGateTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");
    public void Dispose() { try { File.Delete(_db); } catch { } }

    private async Task<(ApprovalGate gate, IAuditLog audit)> MakeGateAsync()
    {
        var audit = new SqliteAuditLog(_db);
        await audit.InitializeAsync();
        return (new ApprovalGate(audit, NullLogger<ApprovalGate>.Instance), audit);
    }

    [Fact]
    public async Task Read_Is_Auto_Approved()
    {
        var (gate, _) = await MakeGateAsync();
        var decision = await gate.RequestAsync(new HostActionRequest("read-file", RiskTier.Read, "r", null, "op", "s"));
        Assert.True(decision.Approved);
    }

    [Fact]
    public async Task Timeout_Defaults_To_Deny()
    {
        var (gate, _) = await MakeGateAsync();
        gate.DefaultTimeout = TimeSpan.FromMilliseconds(200);
        var decision = await gate.RequestAsync(new HostActionRequest("write-file", RiskTier.Write, "w", null, "op", "s"));
        Assert.False(decision.Approved);
        Assert.Contains("Timed out", decision.Reason);
    }

    [Fact]
    public async Task Batch_Token_Grants_Within_Window_And_Tier()
    {
        var (gate, _) = await MakeGateAsync();
        gate.GrantBatchToken("sess-1", RiskTier.Write, TimeSpan.FromSeconds(5));
        var decision = await gate.RequestAsync(new HostActionRequest("s", RiskTier.Write, "w", null, "op", "sess-1"));
        Assert.True(decision.Approved);
        Assert.True(decision.UsedBatchToken);
    }

    [Fact]
    public async Task Operator_Can_Resolve_Pending_Approval()
    {
        var (gate, _) = await MakeGateAsync();
        gate.DefaultTimeout = TimeSpan.FromSeconds(5);

        var pendingTask = gate.RequestAsync(new HostActionRequest("s", RiskTier.Delete, "d", null, "op", "sess"));
        var pending = await gate.PendingReader.ReadAsync();
        gate.Resolve(pending, approved: true, reason: "operator ok");

        var decision = await pendingTask;
        Assert.True(decision.Approved);
        Assert.Equal("operator ok", decision.Reason);
    }
}
