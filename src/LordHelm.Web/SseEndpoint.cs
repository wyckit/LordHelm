using System.Text.Json;
using LordHelm.Monitor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LordHelm.Web;

public static class SseEndpoint
{
    /// <summary>
    /// Maps GET /logs/{subprocessId} as an SSE stream of <see cref="ProcessEvent"/>s for the
    /// requested subprocess. Emits an <c>event: hello</c> frame immediately so EventSource's
    /// <c>readyState</c> transitions to OPEN on the client side even if the subprocess is idle.
    /// On client disconnect (HttpContext.RequestAborted) the subscription is released.
    /// </summary>
    public static IEndpointRouteBuilder MapHelmLogStream(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/logs/{subprocessId}", async (
            string subprocessId,
            HttpContext ctx,
            SseLogBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            await ctx.Response.WriteAsync($"retry: 2000\nevent: hello\ndata: {{\"subprocess\":\"{subprocessId}\"}}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            await using var subscription = broadcaster.Subscribe(subprocessId);
            try
            {
                await foreach (var ev in subscription.Reader.ReadAllAsync(ct))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        kind = ev.Kind.ToString(),
                        line = ev.Line,
                        exitCode = ev.ExitCode,
                        at = ev.At.ToUnixTimeMilliseconds(),
                        label = ev.Label,
                    });
                    await ctx.Response.WriteAsync($"id: {ev.At.ToUnixTimeMilliseconds()}\nevent: log\ndata: {payload}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected; normal shutdown.
            }
        });
        return endpoints;
    }
}
