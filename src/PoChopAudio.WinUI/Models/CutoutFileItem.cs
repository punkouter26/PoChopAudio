using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class CutoutFileItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string? _localFilePath;

    [ObservableProperty]
    private long _bytes;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private string? _jobId;

    [ObservableProperty]
    private ItemProcessingStatus _status = ItemProcessingStatus.Queued;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private BitmapImage? _originalImage;

    [ObservableProperty]
    private BitmapImage? _cutoutImage;

    [ObservableProperty]
    private CutoutKnobsModel _settings = new();

    public bool IsReady => Status == ItemProcessingStatus.Ready;
    public string DetailsText => $"{Width} × {Height} px · {Bytes:N0} bytes";
}
