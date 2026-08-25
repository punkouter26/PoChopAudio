using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Live camera preview and still capture.
///
/// WinUI 3 has no <c>CaptureElement</c> — that was UWP — so the viewfinder is built from a
/// <see cref="MediaFrameReader"/>: frames arrive as BGRA8 software bitmaps and the page pushes each
/// one into a <c>SoftwareBitmapSource</c>. Stills come from the most recent preview frame rather
/// than <c>CapturePhotoToStreamAsync</c>, which fights the frame reader for the device on many
/// webcams. Preview resolution is ample for a head shot and the capture is instant.
/// </summary>
public sealed class CameraService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latest;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Raised per frame with a BGRA8 premultiplied bitmap. The handler must copy what it needs and
    /// must not retain the instance — it is disposed as soon as the handler returns.
    /// </summary>
    public event Action<SoftwareBitmap>? FrameArrived;

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;

        try
        {
            var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                // Cpu memory is what makes frames arrive as SoftwareBitmap rather than a GPU surface.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });

            var source = capture.FrameSources
                .FirstOrDefault(s => s.Value.Info.SourceKind == MediaFrameSourceKind.Color).Value;

            if (source is null)
            {
                capture.Dispose();
                return false;
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.FrameArrived += OnFrameArrived;

            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                return false;
            }

            _capture = capture;
            _reader = reader;
            IsRunning = true;
            return true;
        }
        catch
        {
            // No camera, no permission, or the device is already held exclusively. The caller
            // reports it; everything else on the page keeps working.
            await StopAsync();
            return false;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) return;

        // SoftwareBitmapSource only accepts Bgra8 premultiplied.
        var display = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                      && bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var still = SoftwareBitmap.Copy(display);
        var previous = Interlocked.Exchange(ref _latest, still);
        previous?.Dispose();

        try
        {
            FrameArrived?.Invoke(display);
        }
        finally
        {
            display.Dispose();
        }
    }

    /// <summary>Encodes the most recent preview frame as PNG. Null when no frame has arrived yet.</summary>
    public async Task<byte[]?> CapturePngAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_latest is null) return null;

            using var source = SoftwareBitmap.Copy(_latest);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            // The encoder rejects premultiplied alpha; the preview is opaque anyway.
            using var opaque = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            encoder.SetSoftwareBitmap(opaque);
            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        IsRunning = false;

        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try
            {
                await _reader.StopAsync();
            }
            catch
            {
                // Already stopped or the device vanished; nothing left to do.
            }

            _reader.Dispose();
            _reader = null;
        }

        _capture?.Dispose();
        _capture = null;

        Interlocked.Exchange(ref _latest, null)?.Dispose();
    }

    public void Dispose()
    {
        _ = StopAsync();
        _gate.Dispose();
    }
}
