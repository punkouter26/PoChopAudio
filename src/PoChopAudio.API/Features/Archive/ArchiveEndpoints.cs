using Microsoft.AspNetCore.Http.HttpResults;
using PoChopAudio.API.Storage;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Archive;

public static class ArchiveEndpoints
{
    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/archive")
            .WithTags("Archive")
            .DisableAntiforgery();

        group.MapGet("/batches", ListBatches)
            .WithSummary("List the most recent batches persisted in Azurite");

        group.MapGet("/batches/{batchId}", GetBatch)
            .WithSummary("Load a single batch by id");

        group.MapDelete("/batches/{batchId}", DeleteBatch)
            .WithSummary("Forget a batch");

        group.MapPost("/batches", SaveBatch)
            .WithSummary("Persist a batch to Azurite");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<BatchEntry>>> ListBatches(
        JobArchive archive,
        CancellationToken cancellationToken)
    {
        return TypedResults.Ok(await archive.ListAsync(50, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<BatchEntry>, NotFound>> GetBatch(
        string batchId,
        JobArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = await archive.LoadAsync(batchId, cancellationToken).ConfigureAwait(false);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(entry);
    }

    private static Ok DeleteBatch(string batchId, JobArchive archive)
    {
        archive.Delete(batchId);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, JsonHttpResult<SaveBatchError>>> SaveBatch(
        BatchEntry entry,
        JobArchive archive,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var saved = await archive.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            loggerFactory.CreateLogger("ArchiveEndpoints")
                .LogError("Failed to persist batch {BatchId}.", entry.BatchId);
            return TypedResults.Json(
                new SaveBatchError("Could not persist batch to Azurite. Verify the container is running."),
                statusCode: 500);
        }
        return TypedResults.Ok();
    }

    public sealed record SaveBatchError(string Message);
}
