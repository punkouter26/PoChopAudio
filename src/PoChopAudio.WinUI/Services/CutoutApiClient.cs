using System.Net.Http.Headers;
using System.Net.Http.Json;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Services;

public sealed class CutoutApiClient(IApiConfiguration config)
{
    public async Task<CutoutCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            return await client.GetFromJsonAsync<CutoutCapabilities>("api/cutout/capabilities", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CutoutUploadResult> UploadAsync(Stream stream, string fileName, string contentType = "image/png", CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);

        var response = await client.PostAsync("api/cutout/upload", form, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<CutoutUploadResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CutoutResult> AnalyzeAsync(string jobId, CutoutOptions options, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var response = await client.PostAsJsonAsync($"api/cutout/{jobId}/analyze", options, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<CutoutResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetCutoutImageAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var response = await client.GetAsync($"api/cutout/{jobId}/image", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> GetBatchZipStreamAsync(IReadOnlyList<string> jobIds, string? template = null, CancellationToken cancellationToken = default)
    {
        using var client = config.CreateClient();
        var v = jobIds.Count;
        var query = string.Join('&', jobIds.Select(j => $"jobs={j}"));
        if (!string.IsNullOrEmpty(template))
        {
            query += $"&template={Uri.EscapeDataString(template)}";
        }
        var url = $"api/cutout/images.zip?v={v}&{query}";

        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> DownloadModelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            var response = await client.GetAsync("api/cutout/model", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore if missing on server
        }
        return null;
    }

    public async Task<IReadOnlyList<BatchEntry>?> ListBatchesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            return await client.GetFromJsonAsync<List<BatchEntry>>("api/archive/batches", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            await client.DeleteAsync($"api/archive/batches/{batchId}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
    }

    public async Task SaveBatchAsync(BatchEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            await client.PostAsJsonAsync("api/archive/batches", entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore
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

