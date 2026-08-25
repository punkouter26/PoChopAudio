using Microsoft.Extensions.Logging.Abstractions;
using PoChopAudio.Services;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Cutout.Engines;
using PoChopAudio.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// Exercises the cutout pipeline end to end: upload, engine, edge processing, PNG out. The
/// OnnxU2Net engine only runs when its model file is present, so the inference test skips itself
/// on a machine that has not fetched the model.
///
/// These ran over HTTP until the API was removed. The service is what the desktop app calls, so
/// this is now testing the same path the app takes rather than one layer above it.
/// </summary>
public sealed class CutoutPipelineTests : IDisposable
{
    private readonly CutoutJobStore _store = new();
    private readonly CutoutService _cutout;
    private readonly string _modelPath;

    public CutoutPipelineTests()
    {
        _modelPath = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "u2netp.onnx");
        var options = new CutoutModelOptions(_modelPath);
        var remover = new OnnxU2NetRemover(options, NullLogger<OnnxU2NetRemover>.Instance);
        var picker = new EnginePicker([remover], NullLogger<EnginePicker>.Instance);

        _cutout = new CutoutService(_store, picker, new ProgressChannel(), NullLoggerFactory.Instance);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void CapabilitiesReportTheFormatsThisBuildAccepts()
    {
        var caps = _cutout.GetCapabilities();

        Assert.Contains(".jpg", caps.SupportedExtensions);
        Assert.Equal(CutoutLimits.MaxBatchFiles, caps.MaxBatchFiles);
    }

    [Fact]
    public async Task UploadRejectsOversizedFiles()
    {
        using var source = new MemoryStream(new byte[16]);
        var outcome = await _cutout.UploadAsync(source, "huge.jpg", CutoutLimits.MaxUploadBytes + 1);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.TooLarge, outcome.Failure);
    }

    [Fact]
    public async Task UploadRejectsUnsupportedExtension()
    {
        using var source = new MemoryStream(new byte[16]);
        var outcome = await _cutout.UploadAsync(source, "file.gif", 16);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.UnsupportedMedia, outcome.Failure);
    }

    [Fact]
    public async Task UploadRejectsAnEmptyPayload()
    {
        using var source = new MemoryStream();
        var outcome = await _cutout.UploadAsync(source, "empty.png", 0);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.Empty, outcome.Failure);
    }

    [Fact]
    public async Task UploadAndImageRoundTripsRgbaAsPng()
    {
        var png = BuildTinyPng(64, 64);
        using var source = new MemoryStream(png);

        var upload = await _cutout.UploadAsync(source, "test.png", png.Length);
        Assert.True(upload.IsSuccess, upload.Message);
        Assert.Equal(64, upload.Value.Width);
        Assert.Equal(64, upload.Value.Height);

        var image = _cutout.GetImage(upload.Value.JobId);
        Assert.True(image.IsSuccess, image.Message);
        AssertIsPng(image.Value.Content);
    }

    [Fact]
    public async Task RealOnnxPathProducesNonEmptyPng()
    {
        if (!File.Exists(_modelPath))
        {
            // The model is fetched by SCRIPTS/download-models.ps1; skip rather than fail without it.
            return;
        }

        var png = BuildTinyPng(64, 64);
        using var source = new MemoryStream(png);

        var upload = await _cutout.UploadAsync(source, "test.png", png.Length);
        Assert.True(upload.IsSuccess, upload.Message);

        var analyze = await _cutout.AnalyzeAsync(
            upload.Value.JobId,
            new CutoutOptions { Engine = CutoutEngine.OnnxU2Net });

        Assert.True(analyze.IsSuccess, analyze.Message);
        Assert.Equal(CutoutEngine.OnnxU2Net, analyze.Value.Engine);

        var image = _cutout.GetImage(upload.Value.JobId);
        Assert.True(image.IsSuccess, image.Message);
        Assert.Equal(ExportedFile.Png, image.Value.ContentType);
        AssertIsPng(image.Value.Content);
    }

    private static void AssertIsPng(byte[] bytes)
    {
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    private static byte[] BuildTinyPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cx = width / 2;
                var cy = height / 2;
                var inside = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < (width * width / 16);
                image[x, y] = inside
                    ? new Rgba32(255, 0, 0, 255)
                    : new Rgba32(0, 0, 255, 255);
            }
        }

        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }
}
