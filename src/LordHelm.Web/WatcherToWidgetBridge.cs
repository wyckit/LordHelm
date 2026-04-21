using LordHelm.Monitor;
using Microsoft.Extensions.Hosting;

namespace LordHelm.Web;

/// <summary>
/// Drains <see cref="IProcessMonitor.Events"/> into the Blazor <see cref="WidgetState"/>
/// so subprocess lifecycles auto-materialise and update widgets without manual UI code.
/// </summary>
public sealed class WatcherToWidgetBridge : BackgroundService
{
    private readonly IProcessMonitor _monitor;
    private readonly WidgetState _widgets;

    public WatcherToWidgetBridge(IProcessMonitor monitor, WidgetState widgets)
    {
        _monitor = monitor;
        _widgets = widgets;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var ev in _monitor.Events.ReadAllAsync(ct))
        {
            _widgets.ApplyProcessEvent(ev);
        }
    }
}
