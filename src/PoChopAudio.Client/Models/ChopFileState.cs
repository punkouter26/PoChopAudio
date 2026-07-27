using PoChopAudio.Shared;

namespace PoChopAudio.Client.Models;

/// <summary>Where one file in the batch has got to.</summary>
public enum ChopFileStatus
{
    Queued,
    Uploading,
    Splitting,
    Ready,
    Failed,
}

/// <summary>
/// The detection knobs as the UI holds them. <see cref="ChopOptions"/> carries a null threshold to
/// mean "auto"; the UI has to remember the slider position across a toggle, so it keeps both.
/// </summary>
public sealed class ChopSettings
{
    public int ExpectedSegments { get; set; }
    public double MinSegmentMs { get; set; }
    public double MinGapMs { get; set; }
    public double PadMs { get; set; }
    public bool AutoThreshold { get; set; }
    public double ThresholdDb { get; set; }

    public const double DefaultThresholdDb = -40;

    public static ChopSettings Defaults()
    {
        var defaults = new ChopOptions();
        return new ChopSettings
        {
            ExpectedSegments = defaults.ExpectedSegments,
            MinSegmentMs = defaults.MinSegmentMs,
            MinGapMs = defaults.MinGapMs,
            PadMs = defaults.PadMs,
            AutoThreshold = true,
            ThresholdDb = DefaultThresholdDb,
        };
    }

    public ChopSettings Clone() => (ChopSettings)MemberwiseClone();

    public ChopOptions ToOptions() => new()
    {
        ExpectedSegments = ExpectedSegments,
        MinSegmentMs = MinSegmentMs,
        MinGapMs = MinGapMs,
        PadMs = PadMs,
        ThresholdDb = AutoThreshold ? null : ThresholdDb,
    };
}

/// <summary>One uploaded recording inside a batch: its progress, its split, and its own knobs.</summary>
public sealed class ChopFileState
{
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required ChopSettings Settings { get; set; }

    public ChopFileStatus Status { get; set; } = ChopFileStatus.Queued;
    public UploadResult? Upload { get; set; }
    public AnalysisResult? Analysis { get; set; }
    public string? Error { get; set; }

    /// <summary>Set once this file's knobs are touched, which excludes it from batch-wide re-splits.</summary>
    public bool UsesOwnSettings { get; set; }

    public bool Expanded { get; set; }

    /// <summary>Bumped on every re-split so the browser does not replay a cached clip.</summary>
    public int Version { get; set; }

    public IReadOnlyList<ChopSegment> Segments => Analysis?.Segments ?? [];

    public bool IsReady => Status is ChopFileStatus.Ready && Segments.Count > 0;

    public bool IsBusy => Status is ChopFileStatus.Uploading or ChopFileStatus.Splitting;

    /// <summary>True when the split finished but did not produce what the settings asked for.</summary>
    public bool NeedsAttention =>
        Status is ChopFileStatus.Failed ||
        (Status is ChopFileStatus.Ready && Segments.Count != Settings.ExpectedSegments);

    public double ThresholdValue => Settings.AutoThreshold
        ? Analysis?.ThresholdDb ?? ChopSettings.DefaultThresholdDb
        : Settings.ThresholdDb;
}
