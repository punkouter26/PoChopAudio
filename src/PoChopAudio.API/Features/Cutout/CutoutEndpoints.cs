using Microsoft.AspNetCore.Http.HttpResults;
using PoChopAudio.Services;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Cutout;

/// <summary>
/// HTTP surface over <see cref="CutoutService"/>. Two routes have no service counterpart because
/// they exist only for the browser client: <c>/model</c> ships it the ONNX file, and
/// <c>/progress</c> republishes the progress channel as server-sent events.
/// </summary>
public static class CutoutEndpoints
{
    public static IEndpointRouteBuilder MapCutoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cutout")
            .WithTags("Cutout")
            .DisableAntiforgery();

        group.MapPost("/upload", UploadAsync)
            .WithSummary("Upload an image and decode it for cutout");

        group.MapGet("/capabilities", (CutoutService service) => TypedResults.Ok(service.GetCapabilities()))
            .WithSummary("Image formats and engines available on this server");

        // HEAD as well as GET: the browser engine probes this before offering itself, and a
        // GET-only route lets the probe fall through to the SPA fallback, which answers 200 with
        // index.html and makes a working engine look unavailable.
        group.MapMethods("/model", [HttpMethods.Get, HttpMethods.Head], GetModel)
            .WithSummary("Download u2netp.onnx so the browser engine can run it on-device");

        group.MapPost("/{jobId}/analyze", Analyze)
            .WithSummary("Strip the background from an uploaded image");

        group.MapGet("/{jobId}/image", GetImage)
            .WithSummary("Download the cutout as a single PNG");

        group.MapGet("/{jobId}/progress", GetProgress)
            .WithSummary("Server-sent events stream of progress updates for an analyze request");

        group.MapGet("/images.zip", GetBatchZip)
            .WithSummary("Download the cutouts of several uploads as a flat ZIP");

        group.MapDelete("/{jobId}", Delete)
            .WithSummary("Discard an uploaded image");

        return app;
    }

    private const string OnnxContentType = "application/octet-stream";

    /// <summary>
    /// Serves the ONNX model to the browser engine.
    ///
    /// The model ships as content next to the API, not under wwwroot, so nothing was serving it and
    /// the browser engine could never fetch one. It cannot simply be moved into wwwroot either: the
    /// csproj includes it only <c>Condition="Exists(...)"</c>, and a fresh clone has to build and run
    /// without it. Hence an endpoint that 404s when the file is absent, which is exactly what the
    /// browser side needs in order to report itself unavailable rather than feeding an HTML error
    /// page to the ONNX parser.
    /// </summary>
    private static Results<PhysicalFileHttpResult, NotFound<string>> GetModel(CutoutModelOptions model)
    {
        if (!File.Exists(model.ModelPath))
        {
            return TypedResults.NotFound(
                "u2netp.onnx is not present on this server. Run SCRIPTS/download-models.ps1 to fetch it.");
        }

        // The model never changes for a given deployment, so let the browser and its IndexedDB copy
        // keep it rather than re-downloading 4.4 MB on every cold load.
        return TypedResults.PhysicalFile(model.ModelPath, OnnxContentType, enableRangeProcessing: true);
    }

    private static async Task<Results<Ok<CutoutUploadResult>, ProblemHttpResult>> UploadAsync(
        IFormFile file,
        CutoutService service,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var outcome = await service.UploadAsync(stream, file.FileName, file.Length, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess
            ? TypedResults.Ok(outcome.Value)
            : outcome.ToProblem();
    }

    private static async Task<Results<Ok<CutoutResult>, NotFound<string>, ValidationProblem, ProblemHttpResult>> Analyze(
        string jobId,
        CutoutOptions? options,
        CutoutService service,
        CancellationToken cancellationToken)
    {
        var outcome = await service.AnalyzeAsync(jobId, options, cancellationToken).ConfigureAwait(false);

        return outcome.Failure switch
        {
            null => TypedResults.Ok(outcome.Value),
            OutcomeFailure.NotFound => TypedResults.NotFound(outcome.Message),
            OutcomeFailure.Invalid => TypedResults.ValidationProblem(outcome.ToErrorDictionary()),
            _ => outcome.ToProblem(),
        };
    }

    private static async Task GetProgress(
        string jobId,
        ProgressChannel progress,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!CutoutJobId.TryParse(jobId, out var id))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var reader = progress.Subscribe(id.Value);
        try
        {
            // Initial heartbeat so the client knows the stream is live.
            await context.Response.WriteAsync("event: hello\ndata: ok\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            await foreach (var update in reader.ReadAllAsync(cancellationToken))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(update);
                await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected; abandon the channel.
        }
    }

    private static Results<FileContentHttpResult, NotFound<string>> GetImage(string jobId, CutoutService service)
    {
        var outcome = service.GetImage(jobId);

        return outcome.IsSuccess
            ? TypedResults.File(outcome.Value.Content, outcome.Value.ContentType, outcome.Value.FileName)
            : TypedResults.NotFound(outcome.Message);
    }

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetBatchZip(
        string[] jobs,
        string? template,
        CutoutService service)
    {
        var outcome = service.GetBatchZip(jobs, template);

        return outcome.Failure switch
        {
            null => TypedResults.File(outcome.Value.Content, outcome.Value.ContentType, outcome.Value.FileName),
            OutcomeFailure.NotFound => TypedResults.NotFound(outcome.Message),
            _ => TypedResults.ValidationProblem(outcome.ToErrorDictionary()),
        };
    }

    private static NoContent Delete(string jobId, CutoutService service)
    {
        service.Delete(jobId);
        return TypedResults.NoContent();
    }
}
