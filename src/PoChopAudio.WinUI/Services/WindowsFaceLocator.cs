using System.Runtime.InteropServices.WindowsRuntime;
using PoChopAudio.Services.Cutout;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Face detection through <see cref="FaceDetector"/>, which ships with Windows.
///
/// <para>
/// This is why option 4 costs nothing to ship: the OS already has a face detector, so there is no
/// second ONNX model to download, no extra package, and no startup cost beyond one call. The
/// "no second model" rule in <see cref="HeadFinder"/> is about not adding a dependency, and this
/// adds none.
/// </para>
/// <para>
/// Follows the optional-capability pattern: probe once, report through
/// <see cref="IsAvailable"/>, and degrade to the mask-shape logic rather than throwing.
/// </para>
/// </summary>
public sealed class WindowsFaceLocator : IFaceLocator
{
    private readonly Lazy<FaceDetector?> _detector = new(() =>
    {
        try
        {
            return FaceDetector.IsSupported ? FaceDetector.CreateAsync().GetAwaiter().GetResult() : null;
        }
        catch (Exception)
        {
            // A machine without the face-analysis component is a supported configuration.
            return null;
        }
    });

    public bool IsAvailable => _detector.Value is not null;

    public async Task<FaceBox?> LocateAsync(
        byte[] rgba, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        var detector = _detector.Value;
        if (detector is null || width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            // FaceDetector only accepts a handful of pixel formats, and Gray8 is the one it is
            // guaranteed to take. Detection is luminance-based anyway, so nothing is lost.
            var format = BitmapPixelFormat.Gray8;
            if (!FaceDetector.GetSupportedBitmapPixelFormats().Contains(format))
            {
                format = FaceDetector.GetSupportedBitmapPixelFormats().FirstOrDefault();
                if (format == default)
                {
                    return null;
                }
            }

            using var gray = ToGray8(rgba, width, height);
            cancellationToken.ThrowIfCancellationRequested();

            var faces = await detector.DetectFacesAsync(gray).AsTask(cancellationToken).ConfigureAwait(false);
            if (faces is null || faces.Count == 0)
            {
                return null;
            }

            // Largest face wins: on a head shot there is one subject, and any extra detection is
            // a bystander or a false positive, both smaller than the person in front of the camera.
            var best = faces
                .OrderByDescending(f => (long)f.FaceBox.Width * f.FaceBox.Height)
                .First()
                .FaceBox;

            return new FaceBox((int)best.X, (int)best.Y, (int)best.Width, (int)best.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Detection failing is not the caller's problem; the mask-shape fallback covers it.
            return null;
        }
    }

    private static SoftwareBitmap ToGray8(byte[] rgba, int width, int height)
    {
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var p = i * 4;
            // Rec. 601 luma, the weighting face detection expects.
            gray[i] = (byte)(((rgba[p] * 299) + (rgba[p + 1] * 587) + (rgba[p + 2] * 114)) / 1000);
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, width, height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(gray.AsBuffer());
        return bitmap;
    }
}
