namespace PoChopAudio.Client.Models;

/// <summary>Where one captured frame has got to.</summary>
public enum HeadShotStatus
{
    Captured,
    Cutting,
    Ready,
    Failed,
}

/// <summary>
/// One head shot. The bytes live in the browser and are referenced by <see cref="Id"/>; this class
/// holds only what the UI needs to draw a row, which is why a page of 2 MP photographs costs the
/// .NET heap almost nothing.
/// </summary>
public sealed class HeadShot
{
    public required string Id { get; init; }

    /// <summary>Object URL of the frame as shot, kept so a failed cutout can still be reviewed.</summary>
    public required string OriginalUrl { get; init; }

    /// <summary>Position in the set, and the number that ends up in the file name.</summary>
    public required int Index { get; set; }

    public HeadShotStatus Status { get; set; } = HeadShotStatus.Captured;

    public string? CutoutUrl { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? Error { get; set; }

    /// <summary>True when the model kept nothing, which almost always means nobody was in frame.</summary>
    public bool EmptyMask { get; set; }

    public bool IsReady => Status is HeadShotStatus.Ready && CutoutUrl is not null;

    public bool NeedsAttention => Status is HeadShotStatus.Failed || EmptyMask;

    /// <summary>The saved name: Head_1.png, Head_2.png, and so on.</summary>
    public string FileNameFor(string stem) => $"{stem}_{Index}.png";
}
