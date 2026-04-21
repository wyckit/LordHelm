using LordHelm.Providers;

namespace LordHelm.Execution.Tests;

public class RateLimitTests
{
    [Fact]
    public async Task Admits_Up_To_Max_In_Window()
    {
        var g = new RateLimitGovernor(3, TimeSpan.FromSeconds(60));
        await g.WaitAsync();
        await g.WaitAsync();
        await g.WaitAsync();
        Assert.Equal(3, g.InFlight);
    }

    [Fact]
    public async Task Blocks_When_Full_Until_Oldest_Ages_Out()
    {
        var g = new RateLimitGovernor(2, TimeSpan.FromMilliseconds(400));
        await g.WaitAsync();
        await g.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await g.WaitAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 300, $"expected block for ~400ms, got {sw.ElapsedMilliseconds}");
    }
}
