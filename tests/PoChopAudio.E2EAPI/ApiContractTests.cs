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

    [Fact]
    public void CutoutLimits_ConstantsAreValid()
    {
        Assert.Equal(32, CutoutLimits.MaxBatchFiles);
        Assert.Equal(50L * 1024 * 1024, CutoutLimits.MaxUploadBytes);
        Assert.Equal(4096, CutoutLimits.MaxDimension);
    }

    [Fact]
    public void CutoutEngine_HasExactlyTwoValues()
    {
        // remove.bg was removed per the no-paid-services decision.
        var values = Enum.GetValues<CutoutEngine>();
        Assert.Equal(2, values.Length);
        Assert.Contains(CutoutEngine.OnnxU2Net, values);
        Assert.Contains(CutoutEngine.BrowserOnnx, values);
    }

    [Fact]
    public void CutoutJobId_ParseAcceptsValidAndRejectsInvalid()
    {
        Assert.True(CutoutJobId.TryParse(Guid.NewGuid().ToString(), out _));
        Assert.False(CutoutJobId.TryParse(null, out _));
        Assert.False(CutoutJobId.TryParse("", out _));
        Assert.False(CutoutJobId.TryParse(Guid.Empty.ToString(), out _));
    }
}
