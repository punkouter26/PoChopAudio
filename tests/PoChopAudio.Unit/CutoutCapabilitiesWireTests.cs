using System.Text.Json;
using PoChopAudio.Shared;
using Xunit;

namespace PoChopAudio.Unit;

/// <summary>
/// The Cutout page's engine picker came up empty while /api/cutout/capabilities was plainly
/// returning an engine, which puts the fault between the wire and the record.
/// </summary>
public sealed class CutoutCapabilitiesWireTests
{
    // Copied verbatim from a live GET of /api/cutout/capabilities.
    private const string LiveJson =
        """{"supportedExtensions":[".jpg",".jpeg",".png",".webp"],"availableEngines":[0],"maxBatchFiles":32,"maxUploadMb":50,"maxDimension":4096}""";

    [Fact]
    public void TheLiveResponseDeserialisesWithTheClientDefaults()
    {
        // Web defaults are what HttpClient.GetFromJsonAsync uses.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var caps = JsonSerializer.Deserialize<CutoutCapabilities>(LiveJson, options);

        Assert.NotNull(caps);
        Assert.Equal(32, caps!.MaxBatchFiles);
        Assert.Equal([CutoutEngine.OnnxU2Net], caps.AvailableEngines);
        Assert.NotEmpty(caps.AvailableEngines);
    }
}
