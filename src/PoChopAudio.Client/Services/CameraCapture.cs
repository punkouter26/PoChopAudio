using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PoChopAudio.Client.Services;

/// <summary>What the camera reported once it started.</summary>
public sealed record CameraInfo(int Width, int Height, string Label);

/// <summary>A frame that has just been grabbed, before the background is removed.</summary>
public sealed record CapturedShot(string Id, string OriginalUrl, int Width, int Height);

/// <summary>The finished cutout, cropped to the subject.</summary>
/// <param name="EmptyMask">True when the model kept nothing — usually nobody in frame.</param>
public sealed record CutoutShot(string CutoutUrl, int Width, int Height, bool EmptyMask);

/// <summary>
/// Wraps camera.js. Every pixel stays in the browser: the service passes ids and object URLs
/// across the interop boundary and never the image data, so photographs of someone's face are
/// neither uploaded nor copied into the .NET heap.
/// </summary>
public sealed class CameraCapture(IJSRuntime js) : IAsyncDisposable
{
    public bool IsRunning { get; private set; }

    /// <summary>False when the browser cannot open a camera — no getUserMedia, or an insecure origin.</summary>
    public async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>("pochopaudio.camera.isSupported");
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<CameraInfo> StartAsync(ElementReference video, string facingMode = "user")
    {
        var info = await js.InvokeAsync<CameraInfo>("pochopaudio.camera.start", video, facingMode);
        IsRunning = true;
        return info;
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        await js.InvokeVoidAsync("pochopaudio.camera.stop");
    }

    public Task<CapturedShot> CaptureAsync() =>
        js.InvokeAsync<CapturedShot>("pochopaudio.camera.capture").AsTask();

    /// <summary>Removes the background in-browser and crops to the subject's bounding box.</summary>
    public Task<CutoutShot> CutoutAsync(string id, int paddingPx) =>
        js.InvokeAsync<CutoutShot>("pochopaudio.camera.cutout", id, paddingPx).AsTask();

    public ValueTask RemoveAsync(string id) => js.InvokeVoidAsync("pochopaudio.camera.remove", id);

    public ValueTask ClearAsync() => js.InvokeVoidAsync("pochopaudio.camera.clear");

    /// <summary>Builds a ZIP of the finished cutouts in the page and returns an object URL for it.</summary>
    public Task<string> ZipAsync(IReadOnlyList<string> ids, IReadOnlyList<string> fileNames) =>
        js.InvokeAsync<string>("pochopaudio.camera.zip", ids, fileNames).AsTask();

    /// <summary>
    /// Saves an object URL to disk. Needed because a Blazor <c>&lt;a download&gt;</c> cannot point at
    /// a blob the page created without the click being driven from JS.
    /// </summary>
    public ValueTask SaveAsync(string url, string fileName, bool revokeAfter = false) =>
        js.InvokeVoidAsync("pochopaudio.camera.save", url, fileName, revokeAfter);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
            await ClearAsync();
        }
        catch (JSException)
        {
            // The page is going away; the browser reclaims the camera and the object URLs anyway.
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
