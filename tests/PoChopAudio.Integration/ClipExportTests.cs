using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NAudio.Wave;
using PoChopAudio.API.Features.Chop;
using PoChopAudio.Shared;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// Drives the export knobs through the real HTTP pipeline against a synthesised recording, then
/// decodes what came back and measures it. Asserting on the returned samples is the only way to
/// know the query string reached the exporter and the gain landed where it was asked to — a
/// status code proves nothing about audio.
/// </summary>
public sealed class ClipExportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int SampleRate = 48_000;
    private const double TonePeakDb = -12;

    private readonly WebApplicationFactory<Program> _factory;

    public ClipExportTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task PlainDownloadIsUnchangedByAnEmptyExportQuery()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        var bare = await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1");
        var explicitlyNone = await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1?normalize=None");

        Assert.Equal(bare, explicitlyNone);
    }

    [Fact]
    public async Task PeakNormalizationLandsThePeakOnTheTarget()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        var wav = await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1?normalize=Peak&targetDb=-3");
        var (peakDb, _) = Measure(wav);

        Assert.InRange(peakDb, -3.1, -2.9);
    }

    [Fact]
    public async Task LoudnessNormalizationLandsTheClipOnTheLufsTarget()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        var wav = await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1?normalize=Lufs&targetDb=-16");
        var (_, lufs) = Measure(wav);

        Assert.InRange(lufs, -16.3, -15.7);
    }

    [Fact]
    public async Task TheCeilingIsNeverBreachedEvenByAnAbsurdTarget()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        // Asking for 0 LUFS on a -15 LUFS clip wants +15 dB, which would drive the peak well past
        // full scale. The ceiling has to win, and nothing may clip.
        var wav = await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1?normalize=Lufs&targetDb=0&ceilingDb=-1");
        var (peakDb, _) = Measure(wav);

        Assert.InRange(peakDb, -1.15, -0.95);
    }

    [Fact]
    public async Task FadesSilenceTheEdgesOfTheClip()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        var plain = ReadSamples(await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1"));
        var faded = ReadSamples(await client.GetByteArrayAsync($"api/chop/{jobId}/clips/1?fadeInMs=50&fadeOutMs=50"));

        Assert.Equal(plain.Length, faded.Length);
        Assert.True(Math.Abs(faded[0]) <= Math.Abs(plain[0]));
        Assert.InRange(Math.Abs(faded[0]), 0, 0.0005);
        Assert.InRange(Math.Abs(faded[^1]), 0, 0.0005);

        // The middle of the clip must be left alone; a fade is an edge treatment, not a gain.
        var middle = plain.Length / 2;
        Assert.Equal(plain[middle], faded[middle], 3);
    }

    [Fact]
    public async Task ExportOptionsApplyToTheBatchZipToo()
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        using var response = await client.GetAsync($"api/chop/clips.zip?jobs={jobId}&normalize=Peak&targetDb=-3");
        response.EnsureSuccessStatusCode();

        using var archive = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.NotEmpty(archive.Entries);

        using var entry = archive.Entries[0].Open();
        using var buffer = new MemoryStream();
        await entry.CopyToAsync(buffer);

        var (peakDb, _) = Measure(buffer.ToArray());
        Assert.InRange(peakDb, -3.1, -2.9);
    }

    [Theory]
    [InlineData("targetDb=5")]
    [InlineData("targetDb=-999")]
    [InlineData("ceilingDb=3")]
    [InlineData("fadeInMs=-1")]
    [InlineData("fadeOutMs=99999")]
    public async Task OutOfRangeExportOptionsAreRejected(string query)
    {
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client);

        using var response = await client.GetAsync($"api/chop/{jobId}/clips/1?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASilentClipIsLeftAloneRatherThanAmplified()
    {
        // Normalizing digital silence would be a divide by zero dressed up as a feature.
        using var client = _factory.CreateClient();
        var jobId = await UploadAndAnalyzeAsync(client, silent: true);

        using var response = await client.GetAsync($"api/chop/{jobId}/clips/1?normalize=Peak&targetDb=-1");

        // Either no segment was found in silence (404) or the one found came back untouched.
        if (response.StatusCode is HttpStatusCode.OK)
        {
            var samples = ReadSamples(await response.Content.ReadAsByteArrayAsync());
            Assert.All(samples, sample => Assert.InRange(Math.Abs(sample), 0, 0.01f));
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static async Task<string> UploadAndAnalyzeAsync(HttpClient client, bool silent = false)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(BuildRecording(silent)), "file", "takes.wav" },
        };

        using var upload = await client.PostAsync("api/chop/upload", content);
        upload.EnsureSuccessStatusCode();

        var result = await upload.Content.ReadFromJsonAsync<UploadResult>();
        Assert.NotNull(result);

        using var analyze = await client.PostAsJsonAsync(
            $"api/chop/{result!.JobId}/analyze",
            new ChopOptions { ExpectedSegments = 5 });
        analyze.EnsureSuccessStatusCode();

        return result.JobId;
    }

    /// <summary>
    /// Five 400 ms tones separated by 400 ms of very low noise. The noise matters: a detector
    /// handed digital silence has no floor to measure a contrast against.
    /// </summary>
    private static byte[] BuildRecording(bool silent)
    {
        var amplitude = silent ? 0.0 : Math.Pow(10, TonePeakDb / 20);
        var stream = new MemoryStream();

        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            // Deterministic dither so the noise floor is real but the test never flakes.
            var seed = 12345u;
            float Noise()
            {
                seed = (seed * 1664525u) + 1013904223u;
                return ((seed >> 8) / (float)(1 << 24) * 2 - 1) * 0.0005f;
            }

            void WriteNoise(double seconds)
            {
                for (var i = 0; i < (int)(SampleRate * seconds); i++)
                {
                    writer.WriteSample(Noise());
                }
            }

            void WriteTone(double seconds)
            {
                var frames = (int)(SampleRate * seconds);
                for (var i = 0; i < frames; i++)
                {
                    var value = amplitude * Math.Sin(2 * Math.PI * 1000 * i / SampleRate);
                    writer.WriteSample((float)value + Noise());
                }
            }

            WriteNoise(0.3);
            for (var take = 0; take < 5; take++)
            {
                WriteTone(0.4);
                WriteNoise(0.4);
            }
        }

        return stream.ToArray();
    }

    private static float[] ReadSamples(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var samples = new List<float>();
        var buffer = new float[4096];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer[..read]);
        }

        return [.. samples];
    }

    private static (double PeakDb, double Lufs) Measure(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var channels = reader.WaveFormat.Channels;
        var meter = new LoudnessMeter.Accumulator(reader.WaveFormat.SampleRate, channels);
        var provider = reader.ToSampleProvider();
        var buffer = new float[4096 * channels];

        float peak = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            }

            meter.Add(buffer, read);
        }

        return (ClipProcessor.PeakDb(peak), meter.Integrated());
    }
}
