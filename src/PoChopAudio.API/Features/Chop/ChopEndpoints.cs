using Microsoft.AspNetCore.Http.HttpResults;
using PoChopAudio.Services;
using PoChopAudio.Services.Chop;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Chop;

/// <summary>
/// HTTP surface over <see cref="ChopService"/>. Everything here is binding and status-code
/// mapping — the decisions live in the service so the desktop client reaches the same ones.
/// </summary>
public static class ChopEndpoints
{
    public static IEndpointRouteBuilder MapChopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chop")
            .WithTags("Chop")
            .DisableAntiforgery();

        group.MapPost("/upload", UploadAsync)
            .WithSummary("Upload and decode a recording");

        group.MapGet("/capabilities", (ChopService service) => TypedResults.Ok(service.GetCapabilities()))
            .WithSummary("Audio formats and upload limits the running API accepts");

        group.MapPost("/{jobId}/analyze", Analyze)
            .WithSummary("Find the takes inside an uploaded recording");

        group.MapGet("/{jobId}/clips/{index:int}", GetClip)
            .WithSummary("Download one take as a WAV file");

        group.MapGet("/{jobId}/clips.zip", GetZip)
            .WithSummary("Download every take as a ZIP of WAV files");

        group.MapGet("/clips.zip", GetBatchZip)
            .WithSummary("Download the takes of several uploads as one flat ZIP");

        group.MapDelete("/{jobId}", Delete)
            .WithSummary("Discard an uploaded recording and its decoded audio");

        return app;
    }

    private static async Task<Results<Ok<UploadResult>, ProblemHttpResult>> UploadAsync(
        IFormFile file,
        ChopService service,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var outcome = await service.UploadAsync(stream, file.FileName, file.Length, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess
            ? TypedResults.Ok(outcome.Value)
            : outcome.ToProblem();
    }

    private static Results<Ok<AnalysisResult>, NotFound<string>, ValidationProblem> Analyze(
        string jobId,
        ChopOptions options,
        ChopService service)
    {
        var outcome = service.Analyze(jobId, options);

        return outcome.Failure switch
        {
            null => TypedResults.Ok(outcome.Value),
            OutcomeFailure.NotFound => TypedResults.NotFound(outcome.Message),
            _ => TypedResults.ValidationProblem(outcome.ToErrorDictionary()),
        };
    }

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetClip(
        string jobId,
        int index,
        ChopService service,
        [AsParameters] ChopExportQuery export) =>
        ToFileResult(service.GetClip(jobId, index, export.ToOptions()));

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetZip(
        string jobId,
        ChopService service,
        [AsParameters] ChopExportQuery export) =>
        ToFileResult(service.GetZip(jobId, export.ToOptions()));

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetBatchZip(
        string[] jobs,
        ChopService service,
        [AsParameters] ChopExportQuery export) =>
        ToFileResult(service.GetBatchZip(jobs, export.ToOptions()));

    private static NoContent Delete(string jobId, ChopService service)
    {
        service.Delete(jobId);
        return TypedResults.NoContent();
    }

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> ToFileResult(
        Outcome<ExportedFile> outcome) =>
        outcome.Failure switch
        {
            null => TypedResults.File(outcome.Value.Content, outcome.Value.ContentType, outcome.Value.FileName),
            OutcomeFailure.NotFound => TypedResults.NotFound(outcome.Message),
            _ => TypedResults.ValidationProblem(outcome.ToErrorDictionary()),
        };
}
