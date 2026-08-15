using Microsoft.JSInterop;

namespace PoChopAudio.Client.Services;

/// <summary>
/// Re-encodes an image (raw bytes or remote URL) as a small JPEG data URL for the cutout file
/// card previews. Caps the longest edge so a 12 MP head shot does not decode into a 50 MB
/// in-memory bitmap every time the card is expanded.
/// </summary>
public sealed class PreviewService(IJSRuntime js)
{
    /// <summary>Longest-edge cap, in pixels. Mirrors <c>MAX_PREVIEW_EDGE</c> in preview.js.</summary>
    public const int MaxEdge = 512;

    /// <summary>Shrinks raw image bytes (e.g. the user's dropped file) to a small JPEG data URL.</summary>
    public async Task<string> ShrinkBytesAsync(byte[] bytes, string mimeType, int? maxEdge = null)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return await js.InvokeAsync<string>(
                "pochopaudio.preview.shrinkBytes",
                bytes,
                mimeType,
                maxEdge ?? MaxEdge);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Fetches the URL, decodes the image, and returns a small JPEG data URL.</summary>
    public async Task<string> ShrinkUrlAsync(string url, int? maxEdge = null)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        try
        {
            return await js.InvokeAsync<string>(
                "pochopaudio.preview.shrinkUrl",
                url,
                maxEdge ?? MaxEdge);
        }
        catch
        {
            return string.Empty;
        }
    }
}
