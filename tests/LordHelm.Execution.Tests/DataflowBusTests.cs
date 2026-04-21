using LordHelm.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace LordHelm.Execution.Tests;

public class DataflowBusTests
{
    [Fact]
    public async Task Handler_Fires_On_Matching_Write()
    {
        var bus = new DataflowBus(NullLogger<DataflowBus>.Instance);
        var tcs = new TaskCompletionSource<NodeEvent>();
        await bus.SubscribeAsync(
            new SubscriptionSpec("sub1", "ns1", "task-*"),
            e => { tcs.TrySetResult(e); return Task.CompletedTask; });

        await bus.PublishAsync(new NodeEvent(
            new NodeRef("ns1", "task-42", new Dictionary<string, string>()),
            "payload", DateTimeOffset.UtcNow));

        var ev = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("task-42", ev.Node.Id);
    }

    [Fact]
    public async Task Handler_Is_Idempotent_On_Duplicate_Write()
    {
        var bus = new DataflowBus(NullLogger<DataflowBus>.Instance);
        int hits = 0;
        await bus.SubscribeAsync(
            new SubscriptionSpec("sub2", "ns", "a"),
            _ => { Interlocked.Increment(ref hits); return Task.CompletedTask; });

        var ev = new NodeEvent(new NodeRef("ns", "a", new Dictionary<string, string> { ["k"] = "v" }), "x", DateTimeOffset.UtcNow);
        await bus.PublishAsync(ev);
        await bus.PublishAsync(ev);
        await Task.Delay(150);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void TopoSort_Orders_By_Dependencies()
    {
        var nodes = new[]
        {
            new TaskNode("c", "", new[] { "a", "b" }),
            new TaskNode("a", "", Array.Empty<string>()),
            new TaskNode("b", "", new[] { "a" }),
        };
        var sorted = TaskDag.TopoSort(nodes);
        var indexOf = sorted.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.True(indexOf["a"] < indexOf["b"]);
        Assert.True(indexOf["b"] < indexOf["c"]);
    }

    [Fact]
    public void TopoSort_Rejects_Cycles()
    {
        var nodes = new[]
        {
            new TaskNode("x", "", new[] { "y" }),
            new TaskNode("y", "", new[] { "x" }),
        };
        Assert.Throws<InvalidOperationException>(() => TaskDag.TopoSort(nodes));
    }
}
