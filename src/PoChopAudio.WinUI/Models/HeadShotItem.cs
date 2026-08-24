using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PoChopAudio.WinUI.Models;

public partial class HeadShotItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private byte[] _originalJpegBytes = [];

    [ObservableProperty]
    private byte[] _cutoutPngBytes = [];

    [ObservableProperty]
    private BitmapImage? _image;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private bool _isProcessing;

    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width} × {Height} px" : "";
}

