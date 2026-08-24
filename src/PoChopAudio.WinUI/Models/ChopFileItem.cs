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

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private int _sampleRate;

    [ObservableProperty]
    private int _channels;

    [ObservableProperty]
    private double _peakDb;

    [ObservableProperty]
    private double _noiseFloorDb;

    [ObservableProperty]
    private double _detectedThresholdDb;

    [ObservableProperty]
    private IReadOnlyList<float> _waveform = [];

    [ObservableProperty]
    private ObservableCollection<ChopSegment> _segments = [];

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private bool _usesOwnSettings;

    [ObservableProperty]
    private ChopKnobsModel _settings = new();

    [ObservableProperty]
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

    public string AudioInfoText => $"{DurationSeconds:F1}s · {SampleRate} Hz · {Channels} ch · Peak {PeakDb:F1} dB · Noise {NoiseFloorDb:F1} dB · Gate {DetectedThresholdDb:F1} dB";

    public string TakesCountText => $"{Segments.Count} takes";

    public int Version { get; set; } = 1;
}

