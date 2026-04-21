using LordHelm.Monitor;

namespace LordHelm.Execution.Tests;

public class LogRingTests
{
    [Fact]
    public void Wraps_When_Capacity_Exceeded()
    {
        var r = new LogRing(capacity: 3);
        r.Append("a"); r.Append("b"); r.Append("c"); r.Append("d"); r.Append("e");
        Assert.Equal(new[] { "c", "d", "e" }, r.Snapshot());
    }

    [Fact]
    public void Empty_Snapshot_Is_Empty()
    {
        var r = new LogRing(capacity: 3);
        Assert.Empty(r.Snapshot());
    }
}
