using System.Text.Json;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Storage;

/// <summary>
/// Persists batch metadata to Azurite under a "batches/{id}.json" key. Read/write is
/// best-effort: a missing Azurite returns null, never throws, so the in-memory job store
/// keeps working if the container is offline.
/// </summary>
public sealed class JobArchive(AzuriteBlobStore blobs, ILogger<JobArchive> logger)
{
    private const string ContainerPrefix = "batches/";
    private const string MetadataContainerPrefix = "meta/";
    private const int MaxIndexEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<bool> SaveAsync(BatchEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = ContainerPrefix + entry.BatchId + ".json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await blobs.PutAsync(key, bytes, "application/json", cancellationToken).ConfigureAwait(false);
            await AppendToIndexAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not persist batch {BatchId} to Azurite.", entry.BatchId);
            return false;
        }
    }

    public async Task<BatchEntry?> LoadAsync(string batchId, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = ContainerPrefix + batchId + ".json";
            var bytes = await blobs.GetBytesAsync(key, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : JsonSerializer.Deserialize<BatchEntry>(bytes, JsonOptions);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load batch {BatchId} from Azurite.", batchId);
            return null;
        }
    }

    public async Task<IReadOnlyList<BatchEntry>> ListAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        var list = new List<BatchEntry>();
        try
        {
            var indexBytes = await blobs.GetBytesAsync(MetadataContainerPrefix + "index.json", cancellationToken).ConfigureAwait(false);
            if (indexBytes is null)
            {
                return list;
            }

            var ids = JsonSerializer.Deserialize<List<string>>(indexBytes, JsonOptions) ?? [];
            foreach (var id in ids.Take(count))
            {
                var entry = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    list.Add(entry);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not list batches from Azurite.");
        }
        return list;
    }

    public void Delete(string batchId)
    {
        blobs.Delete(ContainerPrefix + batchId + ".json");
    }

    private async Task AppendToIndexAsync(BatchEntry entry, CancellationToken cancellationToken)
    {
        var indexBytes = await blobs.GetBytesAsync(MetadataContainerPrefix + "index.json", cancellationToken).ConfigureAwait(false);
        var ids = indexBytes is null
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(indexBytes, JsonOptions) ?? new List<string>();

        ids.Remove(entry.BatchId);
        ids.Insert(0, entry.BatchId);
        if (ids.Count > MaxIndexEntries)
        {
            foreach (var evicted in ids.Skip(MaxIndexEntries))
            {
                Delete(evicted);
            }
            ids = ids.Take(MaxIndexEntries).ToList();
        }

        var newIndex = JsonSerializer.SerializeToUtf8Bytes(ids, JsonOptions);
        await blobs.PutAsync(MetadataContainerPrefix + "index.json", newIndex, "application/json", cancellationToken).ConfigureAwait(false);
    }
}
