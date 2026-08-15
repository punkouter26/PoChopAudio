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

    /// <summary>Optional solid background colour to fill behind the cutout. Null = transparent.</summary>
    public BackgroundColor? Background { get; init; }

    /// <summary>Crops the output to the alpha bounding box (the subject's tightest rectangle).</summary>
    public bool TrimTransparentEdges { get; init; }

    /// <summary>
    /// Optional padding (in pixels) added to the trimmed bounding box on every side. 0 = tight crop.
    /// </summary>
    public int TrimPaddingPx { get; init; } = 16;
}

/// <summary>24-bit sRGB colour, used for the optional background fill.</summary>
public readonly record struct BackgroundColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
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
