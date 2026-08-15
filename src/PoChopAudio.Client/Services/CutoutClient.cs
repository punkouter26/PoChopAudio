using System.Net;
using System.Net.Http.Json;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Services;

/// <summary>Typed HTTP client for the cutout endpoints.</summary>
public sealed class CutoutClient(HttpClient http)
{
    public async Task<CutoutCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<CutoutCapabilities>("api/cutout/capabilities", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<CutoutUploadResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);

        var response = await http.PostAsync("api/cutout/upload", form, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<CutoutUploadResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CutoutResult> AnalyzeAsync(string jobId, CutoutOptions options, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync($"api/cutout/{jobId}/analyze", options, cancellationToken).ConfigureAwait(false);
        return await ReadOrThrowAsync<CutoutResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public string ImageUrl(string jobId) => $"api/cutout/{jobId}/image";

    public string ProgressUrl(string jobId) => $"api/cutout/{jobId}/progress";

    public string BatchZipUrl(IReadOnlyList<string> jobIds, string? template = null)
    {
        var v = jobIds.Count;
        var query = string.Join('&', jobIds.Select(j => $"jobs={j}"));
        if (!string.IsNullOrEmpty(template))
        {
            query += $"&template={Uri.EscapeDataString(template)}";
        }
        return $"api/cutout/images.zip?v={v}&{query}";
    }

    public async Task<IReadOnlyList<BatchEntry>?> ListBatchesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<BatchEntry>>("api/archive/batches", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        await http.DeleteAsync($"api/archive/batches/{batchId}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveBatchAsync(BatchEntry entry, CancellationToken cancellationToken = default)
    {
        await http.PostAsJsonAsync("api/archive/batches", entry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return body ?? throw new InvalidOperationException("Empty response body.");
        }

        var problem = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {problem}");
    }
}
