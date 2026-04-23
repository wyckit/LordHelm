using LordHelm.Orchestrator.Artifacts;

namespace LordHelm.Web;

/// <summary>
/// Serves binary artifacts from <see cref="IArtifactStore"/> at
/// <c>/artifacts/{id}</c>. Inline text-y artifacts render directly in the
/// dashboard and don't route through this endpoint.
/// </summary>
public static class ArtifactEndpoint
{
    public static WebApplication MapArtifactEndpoint(this WebApplication app)
    {
        app.MapGet("/artifacts/{id}", (string id, IArtifactStore store) =>
        {
            var entry = store.Get(id);
            if (entry is null) return Results.NotFound();
            var stream = store.OpenBinary(id);
            if (stream is null) return Results.NotFound();
            var fileName = string.IsNullOrWhiteSpace(entry.Title) ? id : entry.Title;
            return Results.File(stream, entry.MimeType, fileName);
        });
        return app;
    }
}
