using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Services;

/// <summary>Phase + percent reported by the server for a single cutout job.</summary>
public sealed record CutoutProgress(string Phase, double Percent);

/// <summary>
/// Listens to the server-sent events stream at <c>/api/cutout/{jobId}/progress</c> and surfaces
/// the latest <see cref="CutoutProgress"/> to subscribers. The connection is created lazily on
/// the first call to <see cref="Subscribe"/>.
/// </summary>
public sealed class ProgressStream
{
    private readonly HttpClient _http;

    public ProgressStream(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Reads all progress events for one job until the server closes the stream.</summary>
    public async IAsyncEnumerable<CutoutProgress> Subscribe(string jobId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"api/cutout/{jobId}/progress";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var dataBuffer = new System.Text.StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {

            if (line.Length == 0)
            {
                if (dataBuffer.Length > 0)
                {
                    var json = dataBuffer.ToString();
                    dataBuffer.Clear();
                    CutoutProgress? update = null;
                    try
                    {
                        update = JsonSerializer.Deserialize<CutoutProgress>(json, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        // Skip malformed events; the stream continues.
                    }

                    if (update is not null)
                    {
                        yield return update;
                    }
                }

                continue;
            }

            if (line.StartsWith("data:"))
            {
                dataBuffer.Append(line.AsSpan(5).Trim());
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
