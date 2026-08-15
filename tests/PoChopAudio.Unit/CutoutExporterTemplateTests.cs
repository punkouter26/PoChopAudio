using PoChopAudio.API.Features.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.Unit;

public sealed class CutoutExporterTemplateTests
{
    [Theory]
    [InlineData("{stem}_cutout", "head_shot", 1, 3, "head_shot_cutout.png")]
    [InlineData("{stem}_{index:00}", "head_shot", 7, 12, "head_shot_07.png")]
    [InlineData("{stem}_{index:000}_{total}", "head_shot", 99, 120, "head_shot_099_120.png")]
    [InlineData("passport_{date}", "head_shot", 1, 1, "passport_")] // date is dynamic — just check prefix
    public void ApplyTemplateExpandsTokens(string template, string stem, int index, int total, string expectedStart)
    {
        var name = CutoutExporter.ApplyTemplate(template, stem, index, total);

        Assert.StartsWith(expectedStart, name);
        Assert.EndsWith(".png", name);
    }

    [Fact]
    public void ApplyTemplateAlwaysAppendsPngExtension()
    {
        var name = CutoutExporter.ApplyTemplate("{stem}", "photo", 1, 1);
        Assert.EndsWith(".png", name);
    }
}
