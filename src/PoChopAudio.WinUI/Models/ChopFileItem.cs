using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class ChopFileItem : ObservableObject
{
    [ObservableProperty]
    private string _jobId = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string? _localFilePath;

    [ObservableProperty]
    private long _sizeBytes;

    // AudioInfoText and TakesCountText are computed from these, so every field they read has to
    // raise a change for them too. Without this the card header stayed at "0 takes / 0.0s / 0 Hz"
    // while the waveform underneath correctly showed the takes that had just been detected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private int _sampleRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private int _channels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _peakDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _noiseFloorDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _detectedThresholdDb;

    [ObservableProperty]
    private IReadOnlyList<float> _waveform = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakesCountText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(HasTakes))]
    private ObservableCollection<ChopSegment> _segments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private string? _warning;

    [ObservableProperty]
    private bool _usesOwnSettings;

    [ObservableProperty]
    private ChopKnobsModel _settings = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private ItemProcessingStatus _status = ItemProcessingStatus.Queued;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private int? _playingSegmentIndex;

    [ObservableProperty]
    private double _playheadRatio;

    public bool IsReady => Status == ItemProcessingStatus.Ready && Segments.Count > 0;

    public bool NeedsAttention => Warning is not null || (IsReady && Segments.Count != Settings.ExpectedSegments);

    public bool HasTakes => Segments.Count > 0;

    public string AudioInfoText => $"{DurationSeconds:F1}s · {SampleRate} Hz · {Channels} ch · Peak {PeakDb:F1} dB · Noise {NoiseFloorDb:F1} dB · Gate {DetectedThresholdDb:F1} dB";

    /// <summary>Short, plain-language summary for the record-first view: how long, how many sounds.</summary>
    public string SummaryText => Segments.Count == 1
        ? $"1 sound · {DurationSeconds:F1}s recording"
        : $"{Segments.Count} sounds · {DurationSeconds:F1}s recording";

    public string TakesCountText => Segments.Count == 1 ? "1 sound" : $"{Segments.Count} sounds";

    public int Version { get; set; } = 1;
}

