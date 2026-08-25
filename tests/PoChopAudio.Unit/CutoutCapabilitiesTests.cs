using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.Unit;

public sealed class CutoutCapabilitiesTests
{
    [Fact]
    public void SupportedExtensionsAdvertisesJpegPngWebp()
    {
        var supported = ImageDecoder.SupportedExtensions;

        Assert.Contains(".jpg", supported);
        Assert.Contains(".jpeg", supported);
        Assert.Contains(".png", supported);
        Assert.Contains(".webp", supported);
    }

    [Fact]
    public void IsSupportedExtensionIsCaseInsensitive()
    {
        Assert.True(ImageDecoder.IsSupportedExtension(".JPG"));
        Assert.True(ImageDecoder.IsSupportedExtension(".WEBP"));
        Assert.False(ImageDecoder.IsSupportedExtension(".gif"));
    }

    [Fact]
    public void IsAcceptedContentTypeIgnoresCharset()
    {
        Assert.True(ImageDecoder.IsAcceptedContentType("image/png; charset=binary"));
        Assert.False(ImageDecoder.IsAcceptedContentType("application/json"));
        Assert.False(ImageDecoder.IsAcceptedContentType(""));
    }

    [Fact]
    public void DefaultCapabilitiesAdvertiseCoreFormats()
    {
        var caps = CutoutCapabilities.Default;

        Assert.Equal(32, caps.MaxBatchFiles);
        Assert.Equal(4096, caps.MaxDimension);
        Assert.Contains(".jpg", caps.SupportedExtensions);
    }
}
