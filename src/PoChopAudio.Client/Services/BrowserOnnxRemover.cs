using System.Runtime.Versioning;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Services;

/// <summary>
/// Server-side shim that always defers to the browser's JS runtime. The actual model runs in
/// the browser; the server's role here is to make the IBackgroundRemover contract uniform so
/// the EnginePicker can resolve it like any other engine once the WASM client has loaded
/// onnxruntime-web. In server-only contexts this engine is never available.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserOnnxRemover(BrowserOnnxRuntime runtime) : IBackgroundRemover
{
    public CutoutEngine Engine => CutoutEngine.BrowserOnnx;

    public bool IsAvailable => runtime.IsAvailableAsync().GetAwaiter().GetResult();

    public async Task<byte[]> RemoveAsync(byte[] image, int width, int height, CancellationToken cancellationToken) =>
        await runtime.RemoveAsync(image, width, height, cancellationToken);
}
