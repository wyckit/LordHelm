using LordHelm.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LordHelm.Web;

public sealed record GoalApiRequest(string Goal, string? PreferredVendor = null, string? Model = null, int Priority = 0);

public static class GoalEndpoint
{
    /// <summary>
    /// Maps <c>POST /api/goals</c> — a JSON endpoint that submits a high-level goal for
    /// Lord Helm to decompose + execute. Returns the GoalRunResult (goalId, DAG node
    /// count, outputs per node). External clients (curl, CLI, other agents) use this.
    /// </summary>
    public static IEndpointRouteBuilder MapHelmGoalEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/goals", async (GoalApiRequest body, IGoalRunner runner, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal must be a non-empty string" });

            var req = new GoalRunRequest(body.Goal, body.PreferredVendor, body.Model, body.Priority);
            var result = await runner.RunAsync(req, ct);
            return Results.Ok(result);
        });
        return endpoints;
    }
}
