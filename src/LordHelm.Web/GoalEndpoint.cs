using LordHelm.Orchestrator;
using LordHelm.Orchestrator.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LordHelm.Web;

public sealed record GoalApiRequest(
    string Goal,
    string? PreferredVendor = null,
    string? Model = null,
    string? Tier = null,
    int Priority = 0,
    bool Thinking = false);

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
            IChatDispatcher dispatcher,
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

            // Route through the unified dispatcher so API callers get the
            // same LLM router + safety floor every other surface applies.
            // SkipRouter=true when the API caller provided explicit hints
            // — treat their selection as authoritative but still enforce
            // the safety floor (Delete/Network/Exec → panel/approval).
            var skipRouter = !string.IsNullOrEmpty(body.PreferredVendor) || model is not null;
            var disp = await dispatcher.DispatchAsync(new ChatDispatchRequest(
                Text: body.Goal,
                SessionId: "api-" + Guid.NewGuid().ToString("N")[..8],
                ExplicitVendor: body.PreferredVendor,
                ExplicitTier: body.Tier,
                ExplicitModel: model,
                SkipRouter: skipRouter,
                Thinking: body.Thinking), ct);

            return Results.Ok(new
            {
                plan = new
                {
                    kind = disp.Plan.Kind.ToString(),
                    personaHints = disp.Plan.PersonaHints,
                    rationale = disp.Plan.Rationale,
                    needsPanel = disp.Plan.NeedsPanel,
                    panelSize = disp.Plan.PanelSize,
                },
                goal = disp.GoalResult,
                reply = disp.ReplyText,
                halted = disp.Halted,
            });
        });
        return endpoints;
    }
}
