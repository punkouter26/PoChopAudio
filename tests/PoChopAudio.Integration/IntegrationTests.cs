using Xunit;

namespace PoChopAudio.Integration;

public class IntegrationTests
{
    [Fact]
    public void SystemIntegration_AssemblyLoads()
    {
        Assert.NotNull(typeof(PoChopAudio.API.Features.Chop.SegmentDetector));
    }
}
