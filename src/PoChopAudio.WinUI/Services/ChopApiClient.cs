using System.Net.Http.Headers;
using System.Net.Http.Json;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Services;

public sealed class ChopApiClient(IApiConfiguration config)
{
    public async Task<ChopCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            return await client.GetFromJsonAsync<ChopCapabilities>("api/chop/capabilities", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<UploadResult> UploadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(content, "file", fileName);

        var response = await client.PostAsync("api/chop/upload", form, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<UploadResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AnalysisResult> AnalyzeAsync(string jobId, ChopOptions options, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var response = await client.PostAsJsonAsync($"api/chop/{jobId}/analyze", options, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<AnalysisResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetClipAudioAsync(string jobId, int index, ExportOptions? export = null, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var url = $"api/chop/{jobId}/clips/{index}";
        if (export is not null && !export.IsPassThrough)
        {
            var q = new List<string> { $"normalize={export.Normalize}" };
            if (export.Normalize != NormalizeMode.None)
            {
                q.Add($"targetDb={export.TargetDb:F1}");
                q.Add($"ceilingDb={export.CeilingDb:F1}");
            }
            if (export.FadeInMs > 0) q.Add($"fadeInMs={export.FadeInMs:F0}");
            if (export.FadeOutMs > 0) q.Add($"fadeOutMs={export.FadeOutMs:F0}");
            url += "?" + string.Join('&', q);
        }

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> GetBatchZipStreamAsync(IReadOnlyList<string> jobIds, ExportOptions? export = null, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var v = jobIds.Count;
        var query = string.Join('&', jobIds.Select(j => $"jobs={j}"));
        var url = $"api/chop/clips.zip?v={v}&{query}";

        if (export is not null && !export.IsPassThrough)
        {
            var q = new List<string> { $"normalize={export.Normalize}" };
            if (export.Normalize != NormalizeMode.None)
            {
                q.Add($"targetDb={export.TargetDb:F1}");
                q.Add($"ceilingDb={export.CeilingDb:F1}");
            }
            if (export.FadeInMs > 0) q.Add($"fadeInMs={export.FadeInMs:F0}");
            if (export.FadeOutMs > 0) q.Add($"fadeOutMs={export.FadeOutMs:F0}");
            url += "&" + string.Join('&', q);
        }

        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            await client.DeleteAsync($"api/chop/{jobId}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore best-effort delete failures
        }
    }

    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return body ?? throw new InvalidOperationException("Empty response body received.");
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {error}");
    }
}

