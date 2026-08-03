using Microsoft.AspNetCore.Http.HttpResults;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Chop;

public static class ChopEndpoints
{
    private const string WavContentType = "audio/wav";
    private const string ZipContentType = "application/zip";

    public static IEndpointRouteBuilder MapChopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chop")
            .WithTags("Chop")
            .DisableAntiforgery();

        group.MapPost("/upload", UploadAsync)
            .WithSummary("Upload and decode a recording");

        group.MapGet("/capabilities", () => TypedResults.Ok(new ChopCapabilities(
            SupportedExtensions: AudioDecoder.SupportedExtensions,
            Description: AudioDecoder.SupportedExtensionsDescription,
            MaxBatchFiles: ChopLimits.MaxBatchFiles,
            MaxUploadMb: (int)(ChopLimits.MaxUploadBytes / (1024 * 1024)))))
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
        ChopJobStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return TypedResults.Problem("The uploaded file is empty.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > ChopLimits.MaxUploadBytes)
        {
            return TypedResults.Problem(
                $"The file is larger than the {ChopLimits.MaxUploadBytes / (1024 * 1024)} MB limit.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var extension = Path.GetExtension(file.FileName);
        if (!AudioDecoder.IsSupportedExtension(extension))
        {
            return TypedResults.Problem(
                $"'{extension}' is not a supported audio format. Supported here: {AudioDecoder.SupportedExtensionsDescription}.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var logger = ChopLog.CreateLogger(loggerFactory);
        var job = store.Create(file.FileName);
        var sourcePath = Path.Combine(job.WorkingDirectory, $"source{extension}");

        try
        {
            await using (var target = File.Create(sourcePath))
            {
                await file.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var envelope = await Task.Run(
                () => AudioDecoder.DecodeToCanonical(sourcePath, job.CanonicalPath),
                cancellationToken).ConfigureAwait(false);

            job.Envelope = envelope;
            File.Delete(sourcePath);

            ChopLog.Decoded(logger, file.FileName, job.Id.ToString(), envelope.DurationSeconds, envelope.SampleRate, envelope.Channels);

            return TypedResults.Ok(new UploadResult(
                JobId: job.Id.ToString(),
                FileName: file.FileName,
                DurationSeconds: Math.Round(envelope.DurationSeconds, 3),
                SampleRate: envelope.SampleRate,
                Channels: envelope.Channels,
                PeakDb: Math.Round(envelope.PeakDb, 2),
                NoiseFloorDb: Math.Round(envelope.NoiseFloorDb, 2),
                Waveform: envelope.Waveform));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            store.Remove(job.Id);
            ChopLog.DecodeFailed(logger, file.FileName, exception);
            return TypedResults.Problem(
                $"Could not decode '{file.FileName}'. It may be corrupt or use an unsupported codec.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static Results<Ok<AnalysisResult>, NotFound<string>, ValidationProblem> Analyze(
        string jobId,
        ChopOptions options,
        ChopJobStore store,
        ILoggerFactory loggerFactory)
    {
        if (store.Find(jobId) is not { Envelope: { } envelope } job)
        {
            return TypedResults.NotFound(NotFoundMessage);
        }

        if (Validate(options) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = SegmentDetector.Detect(envelope, options) with { JobId = job.Id.ToString() };
        job.Segments = result.Segments;

        ChopLog.Analyzed(ChopLog.CreateLogger(loggerFactory), job.Id.ToString(), result.Segments.Count, result.ThresholdDb);

        return TypedResults.Ok(result);
    }

    private static Results<FileContentHttpResult, NotFound<string>> GetClip(string jobId, int index, ChopJobStore store)
    {
        if (store.Find(jobId) is not { } job)
        {
            return TypedResults.NotFound(NotFoundMessage);
        }

        if (job.Segments.FirstOrDefault(s => s.Index == index) is not { } segment)
        {
            return TypedResults.NotFound($"Clip {index} does not exist. Run analyze first.");
        }

        return TypedResults.File(
            ClipExporter.RenderClip(job.CanonicalPath, segment),
            WavContentType,
            ClipExporter.ClipFileName(job, segment));
    }

    private static Results<FileContentHttpResult, NotFound<string>> GetZip(string jobId, ChopJobStore store)
    {
        if (store.Find(jobId) is not { } job)
        {
            return TypedResults.NotFound(NotFoundMessage);
        }

        if (job.Segments.Count == 0)
        {
            return TypedResults.NotFound("There is nothing to download. Run analyze first.");
        }

        var stem = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        return TypedResults.File(
            ClipExporter.RenderZip(job),
            ZipContentType,
            $"{(string.IsNullOrWhiteSpace(stem) ? "clips" : stem)}_clips.zip");
    }

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetBatchZip(
        string[] jobs,
        ChopJobStore store)
    {
        if (jobs.Length > ChopLimits.MaxBatchFiles)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["jobs"] = [$"A batch download covers at most {ChopLimits.MaxBatchFiles} recordings."],
            });
        }

        // Unknown ids are skipped rather than fatal: one expired job should not block the rest.
        var ready = jobs
            .Select(store.Find)
            .OfType<ChopJob>()
            .Where(job => job.Segments.Count > 0)
            .ToArray();

        if (ready.Length == 0)
        {
            return TypedResults.NotFound("There is nothing to download. Upload the files again and re-split.");
        }

        return TypedResults.File(ClipExporter.RenderZip(ready), ZipContentType, "clips.zip");
    }

    private static NoContent Delete(string jobId, ChopJobStore store)
    {
        if (store.Find(jobId) is { } job)
        {
            store.Remove(job.Id);
        }

        return TypedResults.NoContent();
    }

    private const string NotFoundMessage = "That upload has expired or was never received. Upload the file again.";

    private static Dictionary<string, string[]> Validate(ChopOptions options)
    {
        var errors = new Dictionary<string, string[]>();

        Check(nameof(options.ExpectedSegments), options.ExpectedSegments is >= 1 and <= 64, "Expect between 1 and 64 sounds.");
        Check(nameof(options.MinSegmentMs), options.MinSegmentMs is >= 10 and <= 60_000, "Minimum length must be 10-60000 ms.");
        Check(nameof(options.MinGapMs), options.MinGapMs is >= 10 and <= 60_000, "Minimum gap must be 10-60000 ms.");
        Check(nameof(options.PadMs), options.PadMs is >= 0 and <= 5_000, "Padding must be 0-5000 ms.");
        Check(nameof(options.ThresholdDb), options.ThresholdDb is null or (>= -100 and <= 0), "Threshold must be -100 to 0 dBFS.");

        return errors;

        void Check(string field, bool ok, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }
    }
}
