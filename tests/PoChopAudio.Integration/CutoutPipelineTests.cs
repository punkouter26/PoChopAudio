using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// Exercises the cutout HTTP pipeline end-to-end against an in-memory test server. The OnnxU2Net
/// engine is wired up but only runs when its model file is present; tests guard against that and
/// skip the inference step if the model is missing so the build still works on any developer
/// machine.
/// </summary>
public sealed class CutoutPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ModelPath = "Content/Models/u2netp.onnx";

    private readonly WebApplicationFactory<Program> _factory;

    public CutoutPipelineTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UploadAndImageEndpointRoundTripsRgbaAsPng()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cutout/capabilities");
        response.EnsureSuccessStatusCode();

        var caps = await response.Content.ReadFromJsonAsync<CutoutCapabilities>();
        Assert.NotNull(caps);
        Assert.Contains(".jpg", caps!.SupportedExtensions);
    }

    [Fact]
    public async Task UploadRejectsOversizedFiles()
    {
        var client = _factory.CreateClient();

        var bytes = new byte[CutoutLimits.MaxUploadBytes + 1];
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "huge.jpg");

        var response = await client.PostAsync("/api/cutout/upload", form);

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task UploadRejectsUnsupportedExtension()
    {
        var client = _factory.CreateClient();

        var bytes = new byte[16];
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "file.gif");

        var response = await client.PostAsync("/api/cutout/upload", form);

        Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task RealOnnxPathProducesNonEmptyPng()
    {
        var env = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var modelFile = Path.Combine(env.ContentRootPath, ModelPath);
        if (!File.Exists(modelFile))
        {
            // The model is downloaded by SCRIPTS/download-models.ps1; tests skip gracefully when it is absent.
            return;
        }

        using var http = _factory.CreateClient();

        // Build a small 64x64 RGBA PNG on the fly.
        var png = BuildTinyPng(64, 64);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(png), "file", "test.png");

        using var uploadResponse = await http.PostAsync("/api/cutout/upload", form);
        uploadResponse.EnsureSuccessStatusCode();

        var upload = await uploadResponse.Content.ReadFromJsonAsync<CutoutUploadResult>();
        Assert.NotNull(upload);
        Assert.Equal(64, upload!.Width);
        Assert.Equal(64, upload.Height);

        // Run the OnnxU2Net engine.
        using var analyzeResponse = await http.PostAsJsonAsync(
            $"/api/cutout/{upload.JobId}/analyze",
            new CutoutOptions { Engine = CutoutEngine.OnnxU2Net });
        analyzeResponse.EnsureSuccessStatusCode();

        var result = await analyzeResponse.Content.ReadFromJsonAsync<CutoutResult>();
        Assert.NotNull(result);
        Assert.Equal(CutoutEngine.OnnxU2Net, result!.Engine);
        Assert.Equal(64, result.Width);

        // The image endpoint should now return a PNG with the cutout applied.
        using var image = await http.GetAsync($"/api/cutout/{upload.JobId}/image");
        image.EnsureSuccessStatusCode();
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        var pngBytes = await image.Content.ReadAsByteArrayAsync();
        Assert.True(pngBytes.Length > 8);
        Assert.Equal(0x89, pngBytes[0]);
        Assert.Equal(0x50, pngBytes[1]);
        Assert.Equal(0x4E, pngBytes[2]);
        Assert.Equal(0x47, pngBytes[3]);
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
                var inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) < (width * width / 16);
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
