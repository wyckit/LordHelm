using LordHelm.Core;
using LordHelm.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Web;

/// <summary>
/// Bridges <see cref="ApprovalGate.PendingReader"/> into the Blazor widget grid.
/// Each pending approval materialises as a <see cref="WidgetKind.Approval"/>
/// widget; when the operator resolves it in the UI, the gate's TaskCompletionSource
/// is completed. On timeout the Blazor side removes the widget.
/// </summary>
public sealed class ApprovalQueueBridge : BackgroundService
{
    private readonly ApprovalGate _gate;
    private readonly WidgetState _widgets;
    private readonly ILogger<ApprovalQueueBridge> _logger;

    public ApprovalQueueBridge(ApprovalGate gate, WidgetState widgets, ILogger<ApprovalQueueBridge> logger)
    {
        _gate = gate;
        _widgets = widgets;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var pending in _gate.PendingReader.ReadAllAsync(ct))
        {
            var id = "approval-" + pending.Request.SkillId + "-" + Guid.NewGuid().ToString("N")[..6];
            _widgets.RegisterPendingApproval(id, pending);
            _logger.LogInformation("Approval queued: {Id} tier={Tier}", id, pending.Request.RiskTier);
        }
    }
}
