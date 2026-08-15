using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoChopAudio.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PoChopAudio.E2EAPI;

/// <summary>
/// End-to-end coverage of the cutout pipeline against the real sample photos in the repo root.
/// Each test uploads a file, checks the upload metadata, runs the ONNX cutout (if the model
/// file is present), and inspects the resulting PNG to verify the alpha mask is non-trivial.
/// The Pixel Motion Photo (the .MP.jpg) is verified to decode its still frame.
/// </summary>
public sealed class CutoutSamplePhotosTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string RepoRoot = LocateRepoRoot();
    private static readonly string ModelPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "PoChopAudio.API", "Content", "Models", "u2netp.onnx");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly bool _modelPresent;

    public CutoutSamplePhotosTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        var path = ModelPath;
        _modelPresent = File.Exists(Path.GetFullPath(path));
    }

    public static IEnumerable<object[]> SamplePhotos()
    {
        var root = RepoRoot;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "PXL_*.jpg").OrderBy(p => p))
        {
            yield return [path];
        }
    }

    [Theory]
    [MemberData(nameof(SamplePhotos))]
    public async Task UploadsAllSamplePhotosAndImageEndpointReturnsPng(string path)
    {
        var fileName = Path.GetFileName(path);
        using var client = _factory.CreateClient();

        var bytes = await File.ReadAllBytesAsync(path);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);

        using var upload = await client.PostAsync("/api/cutout/upload", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = await upload.Content.ReadFromJsonAsync<CutoutUploadResult>();
        Assert.NotNull(meta);
        Assert.Equal(fileName, meta!.FileName);
        Assert.True(meta.Width > 0, $"Width should be positive for {fileName}");
        Assert.True(meta.Height > 0, $"Height should be positive for {fileName}");
        Assert.True(meta.Width <= CutoutLimits.MaxDimension, $"Width should be within limit for {fileName}");
        Assert.True(meta.Height <= CutoutLimits.MaxDimension, $"Height should be within limit for {fileName}");

        // The /image endpoint should return a PNG header regardless of whether the model
        // ran — without the model it re-encodes the original RGBA, which is still a valid PNG.
        using var image = await client.GetAsync($"/api/cutout/{meta.JobId}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        var pngBytes = await image.Content.ReadAsByteArrayAsync();
        Assert.True(pngBytes.Length > 8, $"PNG should be non-empty for {fileName}");
        Assert.True(IsPngHeader(pngBytes), $"Response should be a PNG for {fileName}");
    }

    [Fact]
    public async Task PixelMotionPhotoUploadsAndDecodesStillFrame()
    {
        var motionPhoto = Path.Combine(RepoRoot, "PXL_20260808_201846198.MP.jpg");
        Assert.True(File.Exists(motionPhoto), $"Sample motion photo missing at {motionPhoto}");

        using var client = _factory.CreateClient();
        var bytes = await File.ReadAllBytesAsync(motionPhoto);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", Path.GetFileName(motionPhoto));

        using var upload = await client.PostAsync("/api/cutout/upload", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = await upload.Content.ReadFromJsonAsync<CutoutUploadResult>();
        Assert.NotNull(meta);
        Assert.Equal("PXL_20260808_201846198.MP.jpg", meta!.FileName);
        Assert.True(meta.Width > 0);
        Assert.True(meta.Height > 0);

        // The trailer is stripped: the original file is 5.98 MB (with MP4),
        // the decoded RGBA is exactly width * height * 4 bytes (no MP4 tail).
        Assert.Equal(meta.Width * meta.Height * 4, meta.Bytes);
    }

    [Fact]
    public async Task OnnxCutoutProducesNonTrivialAlphaMask()
    {
        if (!_modelPresent)
        {
            // The model is downloaded by SCRIPTS/download-models.ps1. Skip gracefully when missing.
            return;
        }

        var sample = Path.Combine(RepoRoot, "PXL_20260808_201848217.jpg");
        Assert.True(File.Exists(sample), $"Sample photo missing at {sample}");

        using var client = _factory.CreateClient();
        var jobId = await UploadAsync(client, sample);

        using var analyze = await client.PostAsJsonAsync(
            $"/api/cutout/{jobId}/analyze",
            new CutoutOptions { Engine = CutoutEngine.OnnxU2Net });
        Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);

        var result = await analyze.Content.ReadFromJsonAsync<CutoutResult>();
        Assert.NotNull(result);
        Assert.Equal(CutoutEngine.OnnxU2Net, result!.Engine);
        Assert.True(result.Bytes > 0);

        using var image = await client.GetAsync($"/api/cutout/{jobId}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        var png = await image.Content.ReadAsByteArrayAsync();

        var stats = MeasureAlphaMask(png);
        Assert.True(stats.TransparentPixels > 0,
            $"Expected some transparent pixels after cutout, found {stats.TransparentPixels}.");
        Assert.True(stats.OpaquePixels > 0,
            $"Expected some opaque pixels after cutout, found {stats.OpaquePixels}.");
        Assert.True(stats.TransparentRatio > 0.10,
            $"Expected at least 10% of pixels to be transparent, got {stats.TransparentRatio:P1}.");
        Assert.True(stats.OpaqueRatio > 0.05,
            $"Expected at least 5% of pixels to be opaque (the subject), got {stats.OpaqueRatio:P1}.");
    }

    [Fact]
    public async Task BatchZipEndpointReturnsValidZipWithAllImages()
    {
        var samples = SamplePhotos().Take(3).Select(o => (string)o[0]).ToArray();
        Assert.True(samples.Length >= 2, "Need at least two sample photos for the batch test.");

        using var client = _factory.CreateClient();
        var jobIds = new List<string>();
        foreach (var path in samples)
        {
            jobIds.Add(await UploadAsync(client, path));
        }

        var query = string.Join('&', jobIds.Select(j => $"jobs={j}"));
        using var zip = await client.GetAsync($"/api/cutout/images.zip?{query}");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);

        var bytes = await zip.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);

        // A ZIP file starts with "PK\x03\x04".
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
        Assert.Equal(0x03, bytes[2]);
        Assert.Equal(0x04, bytes[3]);

        // Each image should appear in the ZIP.
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var entries = archive.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(jobIds.Count, entries.Count);
        foreach (var entry in entries)
        {
            Assert.EndsWith("_cutout.png", entry, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<string> UploadAsync(HttpClient client, string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var fileName = Path.GetFileName(path);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);

        using var upload = await client.PostAsync("/api/cutout/upload", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = await upload.Content.ReadFromJsonAsync<CutoutUploadResult>();
        Assert.NotNull(meta);
        return meta!.JobId;
    }

    private static bool IsPngHeader(byte[] bytes)
    {
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        return bytes.Length >= 8 &&
               bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
               bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
    }

    private static AlphaStats MeasureAlphaMask(byte[] png)
    {
        // Sample every Nth pixel to keep the test fast on multi-megapixel images.
        using var image = Image.Load<Rgba32>(png);
        var total = (long)image.Width * image.Height;
        var transparent = 0L;
        var opaque = 0L;

        var step = Math.Max(1, (int)Math.Sqrt(total / 50_000));

        for (var y = 0; y < image.Height; y += step)
        {
            for (var x = 0; x < image.Width; x += step)
            {
                var alpha = image[x, y].A;
                if (alpha == 0) transparent++;
                else if (alpha == 255) opaque++;
            }
        }

        var sampled = transparent + opaque;
        return new AlphaStats(
            TransparentPixels: transparent,
            OpaquePixels: opaque,
            TransparentRatio: sampled == 0 ? 0 : (double)transparent / sampled,
            OpaqueRatio: sampled == 0 ? 0 : (double)opaque / sampled);
    }

    private static string LocateRepoRoot()
    {
        // The test runs from tests/PoChopAudio.E2EAPI/bin/Debug/net10.0 — climb to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PoChopAudio.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record AlphaStats(long TransparentPixels, long OpaquePixels, double TransparentRatio, double OpaqueRatio);
}
