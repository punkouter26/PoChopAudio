using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PoChopAudio.Client.Services;

/// <summary>
/// Plain DTO for a file the user dropped. We cannot construct a Blazor <c>BrowserFile</c> from
/// a dropped item, so the drop shim returns these descriptors and the page reads the bytes via
/// <see cref="OpenStreamAsync"/>.
/// </summary>
public sealed record DroppedFile(string Name, long Size, string ContentType, IJSObjectReference Handle);

/// <summary>
/// Wraps the dropzone.js shim. Files arrive as a flat list of <see cref="DroppedFile"/>
/// records, and the page can call <see cref="OpenStreamAsync"/> to read each file's bytes.
/// </summary>
public sealed class DropZoneService(IJSRuntime js)
{
    /// <summary>Attaches the drop handler to <paramref name="element"/> and subscribes to dropped files.</summary>
    public async Task<IAsyncDisposable> AttachAsync(ElementReference element, Func<IReadOnlyList<DroppedFile>, Task> onFiles)
    {
        var dispatcher = new DropDispatcher(onFiles);
        var handle = await js.InvokeAsync<IJSObjectReference>(
            "pochopaudio.dropzone.attachDropZone",
            element,
            dispatcher.DotnetRef);
        return new Subscription(handle, dispatcher);
    }

    /// <summary>Reads the bytes of one dropped file as a memory buffer.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(DroppedFile file, CancellationToken cancellationToken = default)
    {
        var handle = await file.Handle.InvokeAsync<IJSStreamReference>("stream", cancellationToken).ConfigureAwait(false);
        await using var stream = await handle.OpenReadStreamAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    private sealed class DropDispatcher
    {
        private readonly DotNetObjectReference<CallbackTarget> _ref;

        public DropDispatcher(Func<IReadOnlyList<DroppedFile>, Task> callback)
        {
            _ref = DotNetObjectReference.Create(new CallbackTarget(callback));
        }

        public DotNetObjectReference<CallbackTarget> DotnetRef => _ref;
    }

    private sealed class CallbackTarget
    {
        private readonly Func<IReadOnlyList<DroppedFile>, Task> _callback;

        public CallbackTarget(Func<IReadOnlyList<DroppedFile>, Task> callback)
        {
            _callback = callback;
        }

        [JSInvokable]
        public async Task OnFiles(IReadOnlyList<DroppedDescriptor>? files)
        {
            if (files is null || files.Count == 0) return;
            var mapped = files.Select(f => new DroppedFile(f.Name, f.Size, f.ContentType, f.Handle)).ToArray();
            await _callback(mapped);
        }
    }

    private sealed class DroppedDescriptor
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public IJSObjectReference Handle { get; set; } = null!;
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly IJSObjectReference _handle;
        private readonly DropDispatcher _dispatcher;

        public Subscription(IJSObjectReference handle, DropDispatcher dispatcher)
        {
            _handle = handle;
            _dispatcher = dispatcher;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _handle.InvokeVoidAsync("dispose").ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            _dispatcher.DotnetRef.Dispose();
            await _handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
