using Microsoft.Extensions.Logging;
using PoChopAudio.Shared;

namespace PoChopAudio.Services.Cutout;

/// <summary>One finished cutout: the PNG and the size it ended up.</summary>
public sealed record CutoutPhoto(byte[] Png, int Width, int Height, CutoutEngine Engine);

/// <summary>
/// Turns a photo into a cutout in one call.
///
/// This used to be four: upload, analyze, fetch image, delete — with a job store holding decoded
/// pixels in between. That shape existed because HTTP is stateless and the browser needed a handle
/// to refer back to. In-process there is nothing to refer back to: the caller already holds the
/// bytes, so the job store, its 2 h expiry and its temp directory were pure ceremony.
/// </summary>
public sealed class CutoutService(EnginePicker picker, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = CutoutLog.CreateLogger(loggerFactory);

    public CutoutCapabilities GetCapabilities() => new(
        SupportedExtensions: ImageDecoder.SupportedExtensions,
        AvailableEngines: picker.AvailableEngines,
        MaxBatchFiles: CutoutLimits.MaxBatchFiles,
        MaxUploadMb: (int)(CutoutLimits.MaxUploadBytes / (1024 * 1024)),
        MaxDimension: CutoutLimits.MaxDimension);

    /// <summary>True when some engine is available. False means the model is missing.</summary>
    public bool IsAvailable => picker.AvailableEngines.Count > 0;

    /// <summary>
    /// Decodes the photo, removes the background, cleans the mask edge, crops to the head, and
    /// encodes a PNG.
    /// </summary>
    /// <param name="length">Byte count, passed separately because a stream cannot always report it.</param>
    public async Task<Outcome<CutoutPhoto>> CutOutAsync(
        Stream source,
        string fileName,
        long length,
        CutoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (length == 0)
        {
            return Outcome<CutoutPhoto>.Empty("The photo is empty.");
        }

        if (length > CutoutLimits.MaxUploadBytes)
        {
            return Outcome<CutoutPhoto>.TooLarge(
                $"The photo is larger than the {CutoutLimits.MaxUploadBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(fileName);
        if (!ImageDecoder.IsSupportedExtension(extension))
        {
            return Outcome<CutoutPhoto>.UnsupportedMedia(
                $"'{extension}' is not a supported image format. Supported: {string.Join(", ", ImageDecoder.SupportedExtensions)}.");
        }

        var opts = options ?? new CutoutOptions();
        var engine = picker.Resolve(opts.Engine);

        if (engine is null)
        {
            return Outcome<CutoutPhoto>.EngineUnavailable(
                "Background removal is unavailable because u2netp.onnx is missing. Run SCRIPTS/download-models.ps1.");
        }

        if (Validate(opts) is { Count: > 0 } errors)
        {
            return Outcome<CutoutPhoto>.Invalid(errors);
        }

        try
        {
            var decoded = await Task.Run(
                () => ImageDecoder.Decode(source, fileName),
                cancellationToken).ConfigureAwait(false);

            CutoutLog.Decoded(_logger, fileName, "-", decoded.Width, decoded.Height, decoded.Rgba.Length, decoded.WasMotionPhoto);

            var mask = await engine.RemoveAsync(decoded.Rgba, decoded.Width, decoded.Height, cancellationToken)
                .ConfigureAwait(false);

            return await Task.Run(
                () =>
                {
                    var processed = EdgeProcessor.Apply(mask, decoded.Width, decoded.Height, opts);

                    var rgba = processed;
                    var width = decoded.Width;
                    var height = decoded.Height;

                    var head = HeadFinder.Find(
                        processed, decoded.Width, decoded.Height, opts.TrimPaddingPx, opts.HeadCutBiasPercent);
                    if (!head.Empty)
                    {
                        rgba = HeadFinder.Crop(processed, decoded.Width, head);
                        width = head.Width;
                        height = head.Height;
                    }

                    var png = ImageDecoder.EncodePng(rgba, width, height);
                    CutoutLog.Cutout(_logger, fileName, engine.Engine, width, height, png.Length);

                    return Outcome<CutoutPhoto>.Ok(new CutoutPhoto(png, width, height, engine.Engine));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.CutoutFailed(_logger, fileName, engine.Engine, exception);
            return Outcome<CutoutPhoto>.Undecodable(
                $"Could not cut out '{fileName}'. It may be corrupt or use an unsupported codec.");
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
        Check(nameof(options.TrimPaddingPx), options.TrimPaddingPx is >= 0 and <= 200, "Crop padding must be 0-200 px.");
        Check(nameof(options.HeadCutBiasPercent), options.HeadCutBiasPercent is >= -40 and <= 40, "Head cut must be -40 to +40 %.");

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
