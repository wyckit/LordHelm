using System.Net;
using System.Threading.Channels;
using LordHelm.Monitor;
using LordHelm.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LordHelm.E2E.Tests;

/// <summary>
/// End-to-end test for the Lord Helm Web process. Drives the real ASP.NET Core
/// pipeline through <see cref="WebApplicationFactory{TEntryPoint}"/>, replacing
/// the Watcher with a test fake so we can drive <see cref="ProcessEvent"/>s
/// deterministically.
///
/// Full SSE byte-level round-trip is covered by <c>SseBroadcasterTests</c>;
/// TestHost buffers streaming response bodies in ways that make that assertion
/// flaky here, so at the HTTP boundary we check only the wire contract
/// (endpoint registered, correct content-type, Cache-Control).
/// </summary>
public class WidgetSseE2ETests : IClassFixture<WidgetSseE2ETests.Factory>
{
    private readonly Factory _factory;
    public WidgetSseE2ETests(Factory factory) { _factory = factory; }

    [Fact(Timeout = 15000)]
    public async Task Sse_Endpoint_Is_Registered_And_Emits_SSE_Content_Type()
    {
        using var client = _factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/logs/target-1");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.GetValues("Cache-Control").FirstOrDefault() ?? "");
    }

    [Fact(Timeout = 15000)]
    public async Task Home_Page_Renders_With_Widget_Grid_Shell()
    {
        using var client = _factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var html = await client.GetStringAsync("/", cts.Token);
        Assert.Contains("Lord Helm", html);
        Assert.Contains("status-summary", html);
        Assert.Contains("goal-bar", html);
    }

    [Fact]
    public async Task Fake_Monitor_Is_Wired_Into_DI()
    {
        // Proves the WebApplicationFactory DI replacement works: after first client,
        // the IProcessMonitor service resolves to our FakeMonitor singleton. Full
        // fan-out behaviour is covered by SseBroadcasterTests at the unit level —
        // TestHost buffers SSE response bodies and delays background-service loop
        // start in ways that make an HTTP round-trip assertion flaky here.
        _ = _factory.CreateClient(); // force host build
        await Task.Yield();
        var resolved = _factory.Services.GetRequiredService<IProcessMonitor>();
        Assert.Same(_factory.Monitor, resolved);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeMonitor Monitor { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(IProcessMonitor) ||
                    d.ServiceType == typeof(Watcher)).ToList();
                foreach (var d in descriptors) services.Remove(d);

                services.AddSingleton(Monitor);
                services.AddSingleton<IProcessMonitor>(Monitor);
            });
        }
    }

    public sealed class FakeMonitor : IProcessMonitor
    {
        private readonly Channel<ProcessEvent> _ch = Channel.CreateUnbounded<ProcessEvent>();
        public ChannelReader<ProcessEvent> Events => _ch.Reader;
        public IReadOnlyDictionary<string, LogRing> Logs { get; } = new Dictionary<string, LogRing>();
        public ProcessHandle Launch(LaunchSpec spec, CancellationToken ct = default) =>
            new(spec.SubprocessId, Task.FromResult(0), new LogRing());
        public ValueTask PublishAsync(ProcessEvent ev) => _ch.Writer.WriteAsync(ev);
    }
}
