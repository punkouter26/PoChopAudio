using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.Unit;

public sealed class CutoutExporterTests
{
    [Theory]
    [InlineData("PXL_20260808_201846198.MP.jpg", "PXL_20260808_201846198")]
    [InlineData("head_shot.jpg", "head_shot")]
    [InlineData(" My.Portrait.PNG ", "My.Portrait")]
    [InlineData("bad:name?.jpg", "bad_name_")]
    public void StemStripsPixelMotionPhotoSuffixAndExtension(string fileName, string expected) =>
        Assert.Equal(expected, CutoutExporter.Stem(fileName));

    [Fact]
    public void ClipFileNameAppendsCutoutSuffix()
    {
        Assert.Equal("head_shot_cutout.png", CutoutExporter.ClipFileName("head_shot"));
    }

    [Fact]
    public void UniqueStemsDisambiguatesRepeats()
    {
        // Three uploads of "head" — the second becomes head(2), and the third is disambiguated
        // against the existing head(2) so every stem is unique.
        var stems = CutoutExporter.UniqueStems(["head.jpg", "head.jpg", "head.jpg"]);

        Assert.Equal(3, stems.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("head", stems[0]);
        Assert.Equal("head(2)", stems[1]);
        Assert.Equal("head(3)", stems[2]);
    }

    [Fact]
    public void Validate_DefaultsAreAccepted()
    {
        var options = new CutoutOptions();
        var errors = InvokeValidate(options);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeFeather()
    {
        var options = new CutoutOptions { FeatherRadius = 10 };
        var errors = InvokeValidate(options);
        Assert.NotEmpty(errors);
        Assert.True(errors.ContainsKey(nameof(options.FeatherRadius)));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeMorphology()
    {
        var options = new CutoutOptions { Morphology = 10 };
        var errors = InvokeValidate(options);
        Assert.NotEmpty(errors);
        Assert.True(errors.ContainsKey(nameof(options.Morphology)));
    }

    [Fact]
    public void Validate_RejectsZeroAlphaMultiplier()
    {
        var options = new CutoutOptions { AlphaMultiplier = 0 };
        var errors = InvokeValidate(options);
        Assert.NotEmpty(errors);
        Assert.True(errors.ContainsKey(nameof(options.AlphaMultiplier)));
    }

    // Validate moved from CutoutEndpoints to CutoutService when the logic left the HTTP layer.
    // It is internal and this project is named in the Services assembly's InternalsVisibleTo, so
    // the reflection workaround this helper used to need is gone.
    private static Dictionary<string, string[]> InvokeValidate(CutoutOptions options) =>
        CutoutService.Validate(options);
}
