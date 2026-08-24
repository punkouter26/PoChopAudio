using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// The browser cutout engine fetches its model over HTTP. Nothing served it for a long time — the
/// client asked for a Razor class library path that does not exist here — and because a Blazor host
/// answers any unmatched GET with index.html and a 200, the failure arrived as an ONNX parse error
/// rather than a 404. These tests pin the two things that made it invisible: the response must be
/// the model rather than an HTML page, and HEAD must work, because that is what the engine probes
/// with before it offers itself.
/// </summary>
public sealed class CutoutModelEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ModelRoute = "/api/cutout/model";
    private static readonly string ModelPath = Path.Combine("Content", "Models", "u2netp.onnx");

    [Fact]
    public async Task GetReturnsTheModelItselfAndNotTheBlazorShell()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(ModelRoute);

        if (!File.Exists(ModelPath))
        {
            // Fresh clone without the model: a clean 404 is the correct answer, never an HTML page.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("text/html", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
            return;
        }

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new FileInfo(ModelPath).Length, body.Length);

        // An ONNX file is a protobuf whose first field is ir_version (tag 0x08). index.html starts '<'.
        Assert.Equal(0x08, body[0]);
        Assert.NotEqual((byte)'<', body[0]);
    }

    [Fact]
    public async Task HeadIsAnsweredByTheEndpointRatherThanTheSpaFallback()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, ModelRoute);
        using var response = await client.SendAsync(request);

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        // The bug: MapGet does not answer HEAD, so the probe fell through to MapFallbackToFile and
        // came back as 200 text/html — which the engine reads as "model unavailable".
        Assert.DoesNotContain("text/html", mediaType);

        if (File.Exists(ModelPath))
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/octet-stream", mediaType);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
