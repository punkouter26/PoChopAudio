namespace PoChopAudio.Shared;

/// <summary>
/// Strips the background from one image and returns an RGBA PNG stream. Implementations are
/// server-side (OnnxU2Net, RemoveBg) or client-side (BrowserOnnx); the contract is the same so
/// the rest of the pipeline does not care which engine produced the alpha.
/// </summary>
public interface IBackgroundRemover
{
    /// <summary>Which engine this implementation represents.</summary>
    CutoutEngine Engine { get; }

    /// <summary>True if all dependencies (model files, API keys, WASM runtime) are present and the engine can be used.</summary>
    bool IsAvailable { get; }

    /// <summary>Strips the background. Output is a PNG byte stream ready to write to disk.</summary>
    /// <param name="image">RGBA pixels, top-down, no padding. Read-only.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PNG-encoded RGBA bytes with the background removed.</returns>
    Task<byte[]> RemoveAsync(
        byte[] image,
        int width,
        int height,
        CancellationToken cancellationToken);
}
