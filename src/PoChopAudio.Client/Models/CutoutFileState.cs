using PoChopAudio.Shared;

namespace PoChopAudio.Client.Models;

/// <summary>Per-file UI state for the cutout page. One row per uploaded image.</summary>
public sealed class CutoutFileState
{
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }

    public CutoutUploadResult? Upload { get; set; }
    public CutoutResult? Result { get; set; }

    public CutoutOptions Settings { get; set; } = new();

    public string? Error { get; set; }

    public bool IsReady => Result is not null && Error is null;

    public bool NeedsAttention => Error is not null;

    public bool Processing { get; set; }

    public bool Expanded { get; set; }

    public int Version { get; set; }
}
