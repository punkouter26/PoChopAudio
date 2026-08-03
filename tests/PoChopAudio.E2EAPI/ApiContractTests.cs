using PoChopAudio.Shared;
using Xunit;

namespace PoChopAudio.E2EAPI;

public class ApiContractTests
{
    [Fact]
    public void ChopLimits_ConstantsAreValid()
    {
        Assert.Equal(32, ChopLimits.MaxBatchFiles);
        Assert.Equal(250L * 1024 * 1024, ChopLimits.MaxUploadBytes);
        Assert.Equal(-40.0, ChopLimits.DefaultThresholdDb);
    }
}
