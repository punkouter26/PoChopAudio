using System.Diagnostics.CodeAnalysis;

namespace PoChopAudio.Shared;

/// <summary>Identifies one uploaded image and its current cutout state.</summary>
public readonly record struct CutoutJobId(Guid Value)
{
    public static CutoutJobId New() => new(Guid.NewGuid());

    public static bool TryParse([NotNullWhen(true)] string? text, out CutoutJobId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new CutoutJobId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("N");
}

/// <summary>Knobs the user can turn when the automatic cutout needs a tweak. All optional.</summary>
public sealed record CutoutOptions
{
    /// <summary>Engine to run. Null asks the API to pick its default.</summary>
    public CutoutEngine? Engine { get; init; }

    /// <summary>Keep pixels above this alpha value (0-255). Lower = more of the image kept.</summary>
    public byte AlphaThreshold { get; init; } = CutoutLimits.DefaultAlphaThreshold;

    /// <summary>Feather radius in pixels. Smooths the mask edge with a box blur.</summary>
    public int FeatherRadius { get; init; } = CutoutLimits.DefaultFeatherRadius;

    /// <summary>Erode (negative) or dilate (positive) the mask by N pixels.</summary>
    public int Morphology { get; init; } = CutoutLimits.DefaultMorphology;

    /// <summary>Final alpha multiplier on the mask.</summary>
    public double AlphaMultiplier { get; init; } = CutoutLimits.DefaultAlphaMultiplier;

    /// <summary>
    /// Optional padding (in pixels) added to the trimmed bounding box on every side. 0 = tight crop.
    /// </summary>
    public int TrimPaddingPx { get; init; } = 16;

    /// <summary>
    /// Snaps every surviving pixel to fully opaque instead of keeping the model's soft alpha.
    /// This is what removes the wispy halo around hair: thresholding alone deletes the faint
    /// pixels but leaves everything above the cut translucent, which reads as a glow.
    /// </summary>
    public bool HardEdge { get; init; } = true;

    /// <summary>
    /// Moves the head/neck cut up (negative) or down (positive), as a percentage of the subject's
    /// height. The neck is found automatically; this is the manual override for when a collar or a
    /// beard makes the automatic choice land wrong.
    /// </summary>
    public int HeadCutBiasPercent { get; init; }

    /// <summary>
    /// Crop the result to the head alone, dropping neck, shoulders and torso. On by default: the
    /// app exists to make head shots, and u2netp is a saliency model that always returns the whole
    /// person. Turn off to keep everything the background removal left behind.
    /// </summary>
    public bool HeadOnly { get; init; } = true;
}

/// <summary>Result of decoding an image upload: enough to draw a preview and decide settings.</summary>
public sealed record CutoutUploadResult(
    string JobId,
    string FileName,
    int Width,
    int Height,
    long Bytes,
    string ContentType);

/// <summary>Result of running one remover. The PNG bytes are returned via a separate download endpoint.</summary>
public sealed record CutoutResult(
    string JobId,
    CutoutEngine Engine,
    int Width,
    int Height,
    long Bytes,
    string? Warning,
    int TrimmedWidth,
    int TrimmedHeight,
    int TrimOffsetX,
    int TrimOffsetY);

/// <summary>Tells the UI which engines, extensions, and limits the running API can actually serve.</summary>
public sealed record CutoutCapabilities(
    IReadOnlyList<string> SupportedExtensions,
    IReadOnlyList<CutoutEngine> AvailableEngines,
    int MaxBatchFiles,
    int MaxUploadMb,
    int MaxDimension)
{
    public static CutoutCapabilities Default { get; } = new(
        SupportedExtensions: [".jpg", ".jpeg", ".png", ".webp"],
        AvailableEngines: [CutoutEngine.OnnxU2Net],
        MaxBatchFiles: CutoutLimits.MaxBatchFiles,
        MaxUploadMb: (int)(CutoutLimits.MaxUploadBytes / (1024 * 1024)),
        MaxDimension: CutoutLimits.MaxDimension);
}
