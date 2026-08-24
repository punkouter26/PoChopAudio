using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace PoChopAudio.WinUI.Services;

public sealed class CameraService : IDisposable
{
    private MediaCapture? _mediaCapture;
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;
    public MediaCapture? Capture => _mediaCapture;

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized && _mediaCapture is not null) return true;

        try
        {
            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto
            };

            await capture.InitializeAsync(settings);
            _mediaCapture = capture;
            _isInitialized = true;
            return true;
        }
        catch
        {
            _isInitialized = false;
            _mediaCapture = null;
            return false;
        }
    }

    public async Task<byte[]?> CapturePhotoAsync()
    {
        if (!_isInitialized || _mediaCapture is null) return null;

        try
        {
            var encoding = ImageEncodingProperties.CreatePng();
            using var stream = new InMemoryRandomAccessStream();
            await _mediaCapture.CapturePhotoToStreamAsync(encoding, stream);

            stream.Seek(0);
            var buffer = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    public async Task<BitmapImage?> CapturePreviewFrameAsync()
    {
        var bytes = await CapturePhotoAsync();
        if (bytes is null || bytes.Length == 0) return null;

        using var ms = new MemoryStream(bytes);
        var ras = ms.AsRandomAccessStream();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(ras);
        return bmp;
    }

    public void Dispose()
    {
        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _isInitialized = false;
    }
}

