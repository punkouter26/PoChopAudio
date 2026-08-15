using System.Runtime.Versioning;
using Microsoft.JSInterop;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Services;

/// <summary>
/// Client-side ONNX Runtime: the model never leaves the user's browser. The C# code marshals the
/// raw image bytes to the JS module, the JS module runs inference and returns the masked PNG.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserOnnxRuntime(IJSRuntime js)
{
    private static bool _availabilityChecked;
    private static bool _available;

    /// <summary>True if the cutout.js shim was loaded and onnxruntime-web is reachable.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        if (_availabilityChecked)
        {
            return _available;
        }

        _availabilityChecked = true;
        try
        {
            _available = await js.InvokeAsync<bool>("pochopaudio.cutout.isAvailable");
        }
        catch
        {
            _available = false;
        }

        return _available;
    }

    /// <summary>Runs the ONNX model against the original encoded image bytes and returns the masked PNG.</summary>
    public async Task<byte[]> RemoveAsync(byte[] image, int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var base64 = await js.InvokeAsync<string>("pochopaudio.cutout.removeBackgroundBytes", cancellationToken, image);
        return string.IsNullOrEmpty(base64) ? image : Convert.FromBase64String(base64);
    }
}
