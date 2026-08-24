using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class CutoutFileItem : ObservableObject
{
    [ObservableProperty]
    private string _jobId = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string? _localFilePath;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private long _bytes;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private CutoutKnobsModel _settings = new();

    [ObservableProperty]
    private ItemProcessingStatus _status = ItemProcessingStatus.Queued;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private BitmapImage? _originalImage;

    [ObservableProperty]
    private BitmapImage? _cutoutImage;

    [ObservableProperty]
    private double _splitOffset = 0.5;

    public bool IsReady => Status == ItemProcessingStatus.Ready && CutoutImage is not null;
}

