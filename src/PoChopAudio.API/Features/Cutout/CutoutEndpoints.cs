using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoChopAudio.Shared;
using PoChopAudio.Services.Cutout;

namespace PoChopAudio.API.Features.Cutout;

public static class CutoutEndpoints
{
    private const string PngContentType = "image/png";
    private const string ZipContentType = "application/zip";

    public static IEndpointRouteBuilder MapCutoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cutout")
            .WithTags("Cutout")
            .DisableAntiforgery();

        group.MapPost("/upload", UploadAsync)
            .WithSummary("Upload an image and decode it for cutout");

        group.MapGet("/capabilities", GetCapabilities)
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
    private static Results<PhysicalFileHttpResult, NotFound<string>> GetModel(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Content", "Models", "u2netp.onnx");

        if (!File.Exists(path))
        {
            return TypedResults.NotFound(
                "u2netp.onnx is not present on this server. Run SCRIPTS/download-models.ps1 to fetch it.");
        }

        // The model never changes for a given deployment, so let the browser and its IndexedDB copy
        // keep it rather than re-downloading 4.4 MB on every cold load.
        return TypedResults.PhysicalFile(path, OnnxContentType, enableRangeProcessing: true);
    }

    private static async Task<Results<Ok<CutoutUploadResult>, ProblemHttpResult>> UploadAsync(
        IFormFile file,
        CutoutJobStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return TypedResults.Problem("The uploaded file is empty.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > CutoutLimits.MaxUploadBytes)
        {
            return TypedResults.Problem(
                $"The file is larger than the {CutoutLimits.MaxUploadBytes / (1024 * 1024)} MB limit.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var extension = Path.GetExtension(file.FileName);
        if (!ImageDecoder.IsSupportedExtension(extension))
        {
            return TypedResults.Problem(
                $"'{extension}' is not a supported image format. Supported: {string.Join(", ", ImageDecoder.SupportedExtensions)}.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        if (!ImageDecoder.IsAcceptedContentType(file.ContentType))
        {
            // Soft warn: the browser sometimes hands us 'application/octet-stream' for drag-drops. We
            // continue as long as the file extension is one we accept.
        }

        var logger = CutoutLog.CreateLogger(loggerFactory);

        try
        {
            await using var stream = file.OpenReadStream();
            var decoded = await Task.Run(() => ImageDecoder.Decode(stream, file.FileName), cancellationToken).ConfigureAwait(false);

            var job = store.Create(file.FileName, decoded.Width, decoded.Height, decoded.Rgba, decoded.ContentType);

            CutoutLog.Decoded(logger, file.FileName, job.Id.ToString(), decoded.Width, decoded.Height, decoded.Rgba.Length, decoded.WasMotionPhoto);

            return TypedResults.Ok(new CutoutUploadResult(
                JobId: job.Id.ToString(),
                FileName: file.FileName,
                Width: decoded.Width,
                Height: decoded.Height,
                Bytes: decoded.Rgba.Length,
                ContentType: decoded.ContentType));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.DecodeFailed(logger, file.FileName, exception);
            return TypedResults.Problem(
                $"Could not decode '{file.FileName}'. It may be corrupt or use an unsupported codec.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static Ok<CutoutCapabilities> GetCapabilities(EnginePicker picker)
    {
        return TypedResults.Ok(new CutoutCapabilities(
            SupportedExtensions: ImageDecoder.SupportedExtensions,
            AvailableEngines: picker.AvailableEngines,
            MaxBatchFiles: CutoutLimits.MaxBatchFiles,
            MaxUploadMb: (int)(CutoutLimits.MaxUploadBytes / (1024 * 1024)),
            MaxDimension: CutoutLimits.MaxDimension));
    }

    private static async Task<Results<Ok<CutoutResult>, NotFound<string>, ValidationProblem, ProblemHttpResult>> Analyze(
        string jobId,
        CutoutOptions? options,
        CutoutJobStore store,
        EnginePicker picker,
        ProgressChannel progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (store.Find(jobId) is not { } job)
        {
            return TypedResults.NotFound(NotFoundMessage);
        }

        var opts = options ?? new CutoutOptions();
        var engine = picker.Resolve(opts.Engine);

        if (engine is null)
        {
            return TypedResults.Problem(
                "No background-removal engine is available. Check the server configuration.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (Validate(opts) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var logger = CutoutLog.CreateLogger(loggerFactory);
        var jobGuid = job.Id.Value;

        try
        {
            progress.Publish(jobGuid, "inferring", 0.10);
            var mask = await engine.RemoveAsync(job.Rgba, job.Width, job.Height, cancellationToken).ConfigureAwait(false);
            progress.Publish(jobGuid, "inferring", 0.55);

            var processed = EdgeProcessor.Apply(mask, job.Width, job.Height, opts);
            progress.Publish(jobGuid, "trimming", 0.70);

            var finalRgba = processed;
            var finalWidth = job.Width;
            var finalHeight = job.Height;
            int offsetX = 0, offsetY = 0;
            if (opts.TrimTransparentEdges)
            {
                var trim = TrimHelper.Trim(processed, job.Width, job.Height, opts.TrimPaddingPx);
                if (trim is not null)
                {
                    finalRgba = trim.Rgba;
                    finalWidth = trim.Width;
                    finalHeight = trim.Height;
                    offsetX = trim.OffsetX;
                    offsetY = trim.OffsetY;
                }
            }

            progress.Publish(jobGuid, "encoding", 0.85);
            var png = ImageDecoder.EncodePng(finalRgba, finalWidth, finalHeight, opts.Background);
            progress.Publish(jobGuid, "done", 1.0);

            // Persist the processed RGBA so subsequent downloads are fast.
            job.Rgba = finalRgba;
            job.LastResult = new CutoutResult(
                job.Id.ToString(),
                engine.Engine,
                finalWidth,
                finalHeight,
                png.Length,
                Warning: null,
                TrimmedWidth: finalWidth,
                TrimmedHeight: finalHeight,
                TrimOffsetX: offsetX,
                TrimOffsetY: offsetY);

            CutoutLog.Cutout(logger, job.Id.ToString(), engine.Engine, finalWidth, finalHeight, png.Length);

            return TypedResults.Ok(job.LastResult);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.CutoutFailed(logger, job.Id.ToString(), engine.Engine, exception);
            return TypedResults.Problem(
                $"Could not remove the background with {engine.Engine}.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        finally
        {
            progress.Complete(jobGuid);
        }
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

    private static Results<FileContentHttpResult, NotFound<string>> GetImage(
        string jobId,
        CutoutJobStore store)
    {
        if (store.Find(jobId) is not { } job)
        {
            return TypedResults.NotFound(NotFoundMessage);
        }

        var bytes = CutoutExporter.RenderPng(job, new CutoutOptions());
        var stem = CutoutExporter.Stem(job.OriginalFileName);
        return TypedResults.File(bytes, PngContentType, CutoutExporter.ClipFileName(stem));
    }

    private static Results<FileContentHttpResult, NotFound<string>, ValidationProblem> GetBatchZip(
        string[] jobs,
        string? template,
        CutoutJobStore store)
    {
        if (jobs.Length > CutoutLimits.MaxBatchFiles)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["jobs"] = [$"A batch download covers at most {CutoutLimits.MaxBatchFiles} images."],
            });
        }

        var ready = jobs
            .Select(store.Find)
            .OfType<CutoutJob>()
            .ToArray();

        if (ready.Length == 0)
        {
            return TypedResults.NotFound("There is nothing to download. Upload the images again.");
        }

        // Every download uses its own default options; clients needing per-file knobs re-call /analyze first.
        var optionsById = ready.ToDictionary(j => j.Id, _ => new CutoutOptions());
        var pattern = string.IsNullOrWhiteSpace(template) ? null : template;

        return TypedResults.File(
            CutoutExporter.RenderZip(ready, optionsById, pattern),
            ZipContentType,
            "cutouts.zip");
    }

    private static NoContent Delete(string jobId, CutoutJobStore store)
    {
        if (store.Find(jobId) is { } job)
        {
            store.Remove(job.Id);
        }

        return TypedResults.NoContent();
    }

    private const string NotFoundMessage = "That upload has expired or was never received. Upload the image again.";

    private static Dictionary<string, string[]> Validate(CutoutOptions options)
    {
        var errors = new Dictionary<string, string[]>();

        Check(nameof(options.AlphaThreshold), options.AlphaThreshold <= 255, "Alpha threshold must be 0-255.");
        Check(nameof(options.FeatherRadius), options.FeatherRadius is >= 0 and <= 5, "Feather radius must be 0-5 px.");
        Check(nameof(options.Morphology), options.Morphology is >= -3 and <= 3, "Morphology must be -3 to +3 px.");
        Check(nameof(options.AlphaMultiplier), options.AlphaMultiplier is > 0 and <= 2, "Alpha multiplier must be 0-2.");
        Check(nameof(options.Engine), options.Engine is null or (CutoutEngine)0 or (CutoutEngine)1, "Unknown engine.");

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
