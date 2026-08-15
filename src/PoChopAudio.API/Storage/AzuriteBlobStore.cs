using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PoChopAudio.API.Storage;

/// <summary>
/// Thin wrapper over an Azurite blob container (mcr.microsoft.com/azure-storage/azurite),
/// run locally however the developer prefers — there is no docker-compose file in this repo.
/// Only batch metadata is persisted here (see JobArchive); the actual audio/image bytes stay
/// on disk in the job's temp directory and are gone once that directory is wiped.
/// </summary>
public sealed class AzuriteBlobStore : IAsyncDisposable
{
    public const string DefaultConnectionString =
        "UseDevelopmentStorage=true";

    public const string DefaultContainer = "pochopaudio";

    private readonly BlobContainerClient _container;
    private readonly ILogger<AzuriteBlobStore> _logger;

    public AzuriteBlobStore(IConfiguration configuration, ILogger<AzuriteBlobStore> logger)
    {
        _logger = logger;
        var connectionString = configuration["Storage:ConnectionString"] ?? DefaultConnectionString;
        var containerName = configuration["Storage:Container"] ?? DefaultContainer;

        var service = new BlobServiceClient(connectionString);
        _container = service.GetBlobContainerClient(containerName);

        // Best-effort create on first call. Done synchronously here (constructor) so the very first
        // PUT doesn't race with the container create. Errors are logged and swallowed so the API
        // can start even when Azurite is down — the in-memory job store still works, only
        // persistence is degraded.
        try
        {
            var response = _container.CreateIfNotExists();
            _logger.LogInformation(
                "Azurite container '{Container}' {Status} at {Endpoint}.",
                containerName,
                response is null ? "existed" : "created",
                service.Uri);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Azurite is not reachable at startup. The job store will still work, but batches won't survive a server restart.");
        }
    }

    /// <summary>Uploads a stream to a key. Returns the absolute blob URL.</summary>
    public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(key);
        var headers = new BlobHttpHeaders { ContentType = contentType };
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, cancellationToken).ConfigureAwait(false);
        return blob.Uri.ToString();
    }

    /// <summary>Uploads raw bytes to a key.</summary>
    public async Task<string> PutAsync(string key, byte[] bytes, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await PutAsync(key, stream, contentType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads a blob to a stream. Returns false if the blob does not exist.</summary>
    public async Task<bool> GetAsync(string key, Stream destination, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(key);
        try
        {
            await blob.DownloadToAsync(destination, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Returns the byte[] or null if not found.</summary>
    public async Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(key);
        try
        {
            using var stream = new MemoryStream();
            await blob.DownloadToAsync(stream, cancellationToken).ConfigureAwait(false);
            return stream.ToArray();
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public bool Exists(string key) => _container.GetBlobClient(key).Exists();

    public void Delete(string key)
    {
        try
        {
            _container.GetBlobClient(key).DeleteIfExists();
        }
        catch (RequestFailedException exception)
        {
            _logger.LogWarning(exception, "Failed to delete blob {Key}.", key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The SDK does not hold long-lived connections, so dispose is a no-op for now.
        await ValueTask.CompletedTask;
    }
}
