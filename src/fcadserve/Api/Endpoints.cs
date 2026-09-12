using FcadServe.Health;
using FcadServe.Jobs;
using FcadServe.Security;
using static FcadServe.Api.ApiModels;

namespace FcadServe.Api;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/jobs", async (SubmitJobRequest? req, JobDirector director, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null)
                return Results.BadRequest(Error("invalid_json", "request body must be JSON."));

            try
            {
                var job = await director.SubmitAsync(req.StlPath, req.OutputDir, ct);
                return Results.Json(SubmitResponse(job.Id, ctx.Request.PathBase.Value ?? ""), statusCode: StatusCodes.Status202Accepted);
            }
            catch (PathValidationException ex)
            {
                var code = StatusCodes.Status400BadRequest;
                if (ex.Code is "output_already_exists" or "artifact_collides_with_input")
                    code = StatusCodes.Status409Conflict;
                return Results.Json(Error(ex.Code, ex.Message), statusCode: code);
            }
            catch (JobQueueFullException ex)
            {
                return Results.Json(Error("queue_full", ex.Message), statusCode: StatusCodes.Status429TooManyRequests);
            }
        });

        v1.MapGet("/jobs/{jobId}", async (string jobId, JobStore store, HttpContext ctx, CancellationToken ct) =>
        {
            var job = await store.GetAsync(jobId, ct);
            return job is null
                ? Results.NotFound(Error("not_found", $"unknown job: {jobId}"))
                : Results.Ok(JobToDto(job, ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value! : ""));
        });

        v1.MapGet("/jobs/{jobId}/artifacts", async (string jobId, JobStore store, CancellationToken ct) =>
        {
            var job = await store.GetAsync(jobId, ct);
            return job is null
                ? Results.NotFound(Error("not_found", $"unknown job: {jobId}"))
                : Results.Ok(ArtifactsToDto(job));
        });

        v1.MapGet("/jobs/{jobId}/artifacts/{name}", async (string jobId, string name, JobStore store, CancellationToken ct) =>
        {
            var job = await store.GetAsync(jobId, ct);
            if (job is null)
                return Results.NotFound(Error("not_found", $"unknown job: {jobId}"));

            var artifact = ArtifactFile.Deserialize(job.ArtifactJson)
                .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            if (artifact is null)
                return Results.NotFound(Error("artifact_not_found", $"no artefact named '{name}' for job {jobId}"));
            if (!File.Exists(artifact.Path))
                return Results.NotFound(Error("artifact_missing", $"artifact '{name}' no longer exists on disk"));

            return Results.File(artifact.Path, contentType: ContentTypeFor(artifact.Name),
                fileDownloadName: artifact.Name, enableRangeProcessing: true);
        });

        v1.MapPost("/jobs/{jobId}/cancel", async (string jobId, JobDirector director, JobStore store, CancellationToken ct) =>
        {
            var job = await store.GetAsync(jobId, ct);
            if (job is null)
                return Results.NotFound(Error("not_found", $"unknown job: {jobId}"));

            var requested = await director.CancelAsync(jobId, ct);
            return Results.Json(new
            {
                job_id = jobId,
                status = (await store.GetAsync(jobId, ct))?.Status.ToString().ToLowerInvariant(),
                cancel_requested = requested,
                status_url = $"/v1/jobs/{jobId}",
            }, statusCode: StatusCodes.Status200OK);
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/readyz", async (ReadinessState readiness, CancellationToken ct) =>
        {
            var checks = await readiness.RunAsync(ct);
            var mapped = checks.ToDictionary(k => k.Key, v => v.Value.Detail);
            return checks.Values.All(c => c.Ok)
                ? Results.Ok(new { ready = true, checks = mapped })
                : Results.Json(new { ready = false, checks = mapped },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }

    private static string ContentTypeFor(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".stp" or ".step" => "application/step",
            ".json" => "application/json",
            ".log" => "text/plain",
            _ => "application/octet-stream",
        };
    }
}