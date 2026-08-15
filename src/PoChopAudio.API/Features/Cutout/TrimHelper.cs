using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Cutout;

/// <summary>
/// Crops an RGBA buffer to the alpha-channel bounding box, with optional padding. Returns the
/// trimmed bytes and the offsets so the caller can report the new dimensions.
/// </summary>
public static class TrimHelper
{
    public sealed record TrimResult(byte[] Rgba, int Width, int Height, int OffsetX, int OffsetY);

    public static TrimResult? Trim(byte[] rgba, int width, int height, int padding)
    {
        var stride = width * CutoutLimits.AlphaChannels;
        int minX = width, minY = height, maxX = -1, maxY = -1;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                if (rgba[row + (x * CutoutLimits.AlphaChannels) + 3] == 0)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
        {
            // Fully transparent — nothing to crop, return the original.
            return null;
        }

        var pad = Math.Max(0, padding);
        var x0 = Math.Max(0, minX - pad);
        var y0 = Math.Max(0, minY - pad);
        var x1 = Math.Min(width - 1, maxX + pad);
        var y1 = Math.Min(height - 1, maxY + pad);
        var newW = x1 - x0 + 1;
        var newH = y1 - y0 + 1;

        var trimmed = new byte[CutoutLimits.AlphaChannels * newW * newH];
        for (var y = 0; y < newH; y++)
        {
            Buffer.BlockCopy(rgba, (y0 + y) * stride + x0 * CutoutLimits.AlphaChannels, trimmed, y * newW * CutoutLimits.AlphaChannels, newW * CutoutLimits.AlphaChannels);
        }

        return new TrimResult(trimmed, newW, newH, x0, y0);
    }
}
