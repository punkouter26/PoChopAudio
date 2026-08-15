using PoChopAudio.API.Features.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.Unit;

public sealed class TrimHelperTests
{
    [Fact]
    public void TrimReturnsNullWhenFullyTransparent()
    {
        var rgba = new byte[CutoutLimits.AlphaChannels * 4 * 4];
        var result = TrimHelper.Trim(rgba, 4, 4, padding: 0);
        Assert.Null(result);
    }

    [Fact]
    public void TrimCropsToAlphaBoundingBox()
    {
        // 8x8 fully transparent, except (3,3) and (5,5) which are opaque.
        var rgba = new byte[CutoutLimits.AlphaChannels * 8 * 8];
        rgba[(3 * 8 + 3) * CutoutLimits.AlphaChannels + 3] = 255;
        rgba[(5 * 8 + 5) * CutoutLimits.AlphaChannels + 3] = 255;

        var result = TrimHelper.Trim(rgba, 8, 8, padding: 0);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Width);
        Assert.Equal(3, result.Height);
        Assert.Equal(3, result.OffsetX);
        Assert.Equal(3, result.OffsetY);
    }

    [Fact]
    public void TrimAddsPadding()
    {
        var rgba = new byte[CutoutLimits.AlphaChannels * 6 * 6];
        rgba[(2 * 6 + 2) * CutoutLimits.AlphaChannels + 3] = 255;

        var result = TrimHelper.Trim(rgba, 6, 6, padding: 2);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Width);
        Assert.Equal(5, result.Height);
        Assert.Equal(0, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
    }
}
