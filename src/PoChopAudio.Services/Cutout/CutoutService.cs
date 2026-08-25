using Microsoft.Extensions.Logging;
using PoChopAudio.Shared;

namespace PoChopAudio.Services.Cutout;

/// <summary>
/// The whole cutout feature, independent of how it is reached. Progress is published to the
/// injected <see cref="ProgressChannel"/>; the API republishes it as server-sent events and a
/// desktop host can subscribe to the same channel directly.
/// </summary>
public sealed class CutoutService(
    CutoutJobStore store,
    EnginePicker picker,
    ProgressChannel progress,
    ILoggerFactory loggerFactory)
{
    private const string NotFoundMessage =
        "That upload has expired or was never received. Upload the image again.";

    private readonly ILogger _logger = CutoutLog.CreateLogger(loggerFactory);

    public CutoutCapabilities GetCapabilities() => new(
        SupportedExtensions: ImageDecoder.SupportedExtensions,
        AvailableEngines: picker.AvailableEngines,
        MaxBatchFiles: CutoutLimits.MaxBatchFiles,
        MaxUploadMb: (int)(CutoutLimits.MaxUploadBytes / (1024 * 1024)),
        MaxDimension: CutoutLimits.MaxDimension);

    /// <summary>Decodes an upload to raw RGBA and keeps it in a job for later analysis.</summary>
    /// <param name="length">Byte count, passed separately because a form stream cannot always report it.</param>
    public async Task<Outcome<CutoutUploadResult>> UploadAsync(
        Stream content,
        string fileName,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (length == 0)
        {
            return Outcome<CutoutUploadResult>.Empty("The uploaded file is empty.");
        }

        if (length > CutoutLimits.MaxUploadBytes)
        {
            return Outcome<CutoutUploadResult>.TooLarge(
                $"The file is larger than the {CutoutLimits.MaxUploadBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(fileName);
        if (!ImageDecoder.IsSupportedExtension(extension))
        {
            return Outcome<CutoutUploadResult>.UnsupportedMedia(
                $"'{extension}' is not a supported image format. Supported: {string.Join(", ", ImageDecoder.SupportedExtensions)}.");
        }

        try
        {
            var decoded = await Task.Run(
                () => ImageDecoder.Decode(content, fileName),
                cancellationToken).ConfigureAwait(false);

            var job = store.Create(fileName, decoded.Width, decoded.Height, decoded.Rgba, decoded.ContentType);

            CutoutLog.Decoded(_logger, fileName, job.Id.ToString(), decoded.Width, decoded.Height, decoded.Rgba.Length, decoded.WasMotionPhoto);

            return Outcome<CutoutUploadResult>.Ok(new CutoutUploadResult(
                JobId: job.Id.ToString(),
                FileName: fileName,
                Width: decoded.Width,
                Height: decoded.Height,
                Bytes: decoded.Rgba.Length,
                ContentType: decoded.ContentType));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.DecodeFailed(_logger, fileName, exception);
            return Outcome<CutoutUploadResult>.Undecodable(
                $"Could not decode '{fileName}'. It may be corrupt or use an unsupported codec.");
        }
    }

    /// <summary>Runs the selected engine, applies the edge knobs, and stores the processed pixels.</summary>
    public async Task<Outcome<CutoutResult>> AnalyzeAsync(
        string? jobId,
        CutoutOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (store.Find(jobId) is not { } job)
        {
            return Outcome<CutoutResult>.NotFound(NotFoundMessage);
        }

        var opts = options ?? new CutoutOptions();
        var engine = picker.Resolve(opts.Engine);

        if (engine is null)
        {
            return Outcome<CutoutResult>.EngineUnavailable(
                "No background-removal engine is available. Check the server configuration.");
        }

        if (Validate(opts) is { Count: > 0 } errors)
        {
            return Outcome<CutoutResult>.Invalid(errors);
        }

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

            CutoutLog.Cutout(_logger, job.Id.ToString(), engine.Engine, finalWidth, finalHeight, png.Length);

            return Outcome<CutoutResult>.Ok(job.LastResult);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.CutoutFailed(_logger, job.Id.ToString(), engine.Engine, exception);
            return Outcome<CutoutResult>.Undecodable(
                $"Could not remove the background with {engine.Engine}.");
        }
        finally
        {
            progress.Complete(jobGuid);
        }
    }

    /// <summary>Renders the cutout of one image as a PNG.</summary>
    public Outcome<ExportedFile> GetImage(string? jobId)
    {
        if (store.Find(jobId) is not { } job)
        {
            return Outcome<ExportedFile>.NotFound(NotFoundMessage);
        }

        var stem = CutoutExporter.Stem(job.OriginalFileName);
        return Outcome<ExportedFile>.Ok(new ExportedFile(
            CutoutExporter.RenderPng(job, new CutoutOptions()),
            CutoutExporter.ClipFileName(stem),
            ExportedFile.Png));
    }

    /// <summary>
    /// Renders several cutouts as one flat ZIP. Every download uses its own default options;
    /// clients needing per-file knobs re-call analyze first.
    /// </summary>
    public Outcome<ExportedFile> GetBatchZip(IReadOnlyList<string> jobIds, string? template)
    {
        if (jobIds.Count > CutoutLimits.MaxBatchFiles)
        {
            return Outcome<ExportedFile>.Invalid(
                "jobs", $"A batch download covers at most {CutoutLimits.MaxBatchFiles} images.");
        }

        var ready = jobIds
            .Select(store.Find)
            .OfType<CutoutJob>()
            .ToArray();

        if (ready.Length == 0)
        {
            return Outcome<ExportedFile>.NotFound("There is nothing to download. Upload the images again.");
        }

        var optionsById = ready.ToDictionary(j => j.Id, _ => new CutoutOptions());
        var pattern = string.IsNullOrWhiteSpace(template) ? null : template;

        return Outcome<ExportedFile>.Ok(new ExportedFile(
            CutoutExporter.RenderZip(ready, optionsById, pattern),
            "cutouts.zip",
            ExportedFile.Zip));
    }

    /// <summary>Discards an upload. An unknown id is a no-op, so this is safe to call twice.</summary>
    public void Delete(string? jobId)
    {
        if (store.Find(jobId) is { } job)
        {
            store.Remove(job.Id);
        }
    }

    /// <summary>Field-keyed errors for the edge knobs, empty when they are usable.</summary>
    internal static Dictionary<string, string[]> Validate(CutoutOptions options)
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
