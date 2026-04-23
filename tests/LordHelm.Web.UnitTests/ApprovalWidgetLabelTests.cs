using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Consensus.Tests;

public class ApprovalWidgetLabelTests
{
    [Fact]
    public void Expert_Approval_Renders_Friendly_Label_And_Preview_Lines()
    {
        var state = new WidgetState();
        var req = new HostActionRequest(
            SkillId: "expert:code-auditor",
            RiskTier: RiskTier.Exec,
            Summary: "Code Auditor produced 42-tok output via claude",
            DiffPreview: "line-1\nline-2\nline-3",
            OperatorId: "lord-helm",
            SessionId: "expert-runtime");
        var tcs = new TaskCompletionSource<ApprovalDecision>();
        var pending = new LordHelm.Execution.ApprovalGate.PendingApproval(req, tcs);

        state.RegisterPendingApproval("w1", pending);
        var widget = state.Snapshot().Single(w => w.Id == "w1");

        Assert.Equal("expert approval: code-auditor", widget.Label);
        Assert.Equal(WidgetKind.Approval, widget.Kind);
        Assert.NotNull(widget.Tail);
        Assert.Contains(widget.Tail!, l => l.Contains("tier=Exec"));
        Assert.Contains(widget.Tail!, l => l.Contains("output preview"));
        Assert.Contains(widget.Tail!, l => l.Contains("line-1"));
        Assert.Contains(widget.Tail!, l => l.Contains("line-2"));
    }

    [Fact]
    public void Non_Expert_Approval_Keeps_Bare_Skill_Label()
    {
        var state = new WidgetState();
        var req = new HostActionRequest(
            SkillId: "write-file",
            RiskTier: RiskTier.Write,
            Summary: "write to /tmp/x",
            DiffPreview: null,
            OperatorId: "op",
            SessionId: "s");
        var pending = new LordHelm.Execution.ApprovalGate.PendingApproval(
            req, new TaskCompletionSource<ApprovalDecision>());

        state.RegisterPendingApproval("w2", pending);
        var widget = state.Snapshot().Single(w => w.Id == "w2");

        Assert.Equal("approval: write-file", widget.Label);
        Assert.DoesNotContain(widget.Tail!, l => l.Contains("output preview"));
    }
}
