using LordHelm.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LordHelm.Web;

public sealed record GoalApiRequest(
    string Goal,
    string? PreferredVendor = null,
    string? Model = null,
    string? Tier = null,
    int Priority = 0);

public static class GoalEndpoint
{
    /// <summary>
    /// Maps <c>POST /api/goals</c> — a JSON endpoint that submits a high-level goal for
    /// Lord Helm to decompose + execute. Returns the GoalRunResult (goalId, DAG node
    /// count, outputs per node, synthesis). External clients (curl, CLI, other agents).
    ///
    /// The caller can request a specific model explicitly (<c>model</c>), a vendor
    /// (<c>preferredVendor</c>), and/or a tier (<c>tier: fast|deep|code</c>). When the
    /// caller provides only a tier, the endpoint resolves it against
    /// <see cref="IModelCatalog"/> and the preferred vendor to pick the best concrete
    /// model available at submission time.
    /// </summary>
    public static IEndpointRouteBuilder MapHelmGoalEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/goals", async (
            GoalApiRequest body,
            IGoalRunner runner,
            IModelCatalog catalog,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Goal))
                return Results.BadRequest(new { error = "goal must be a non-empty string" });

            var model = body.Model;
            if (model is null && !string.IsNullOrEmpty(body.Tier)
                && Enum.TryParse<ModelTier>(body.Tier, ignoreCase: true, out var tier))
            {
                model = catalog.Resolve(tier, body.PreferredVendor)?.ModelId;
            }

            var req = new GoalRunRequest(body.Goal, body.PreferredVendor, model, body.Priority);
            var result = await runner.RunAsync(req, ct);
            return Results.Ok(result);
        });
        return endpoints;
    }
}
