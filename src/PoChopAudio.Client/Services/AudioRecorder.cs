using Microsoft.JSInterop;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Services;

/// <summary>A finished recording, already encoded as a WAV the API can decode unaided.</summary>
/// <param name="Wav">16-bit PCM WAV bytes, header included.</param>
/// <param name="Clipped">True if any sample reached the converter's ceiling while recording.</param>
public sealed record RecordingResult(byte[] Wav, int SampleRate, double DurationSeconds, bool Clipped);

/// <summary>Live meter reading, pushed from the audio thread roughly twenty times a second.</summary>
public sealed record RecordingLevel(double PeakDb, double ElapsedSeconds, bool Clipped);

/// <summary>
/// Wraps recorder.js. The browser captures float samples and writes the WAV itself, so a recording
/// reaches <c>/api/chop/upload</c> as an ordinary .wav and needs no server-side codec that would
/// only exist for this one feature.
/// </summary>
public sealed class AudioRecorder(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>
    /// Ceiling on a single recording, left a little under the upload limit so the take that finally
    /// ends is still small enough to post.
    /// </summary>
    public static long MaxRecordingBytes => (long)(ChopLimits.MaxUploadBytes * 0.95);

    private DotNetObjectReference<AudioRecorder>? _self;
    private Action<RecordingLevel>? _onLevel;
    private Action? _onLimitReached;

    public bool IsRecording { get; private set; }

    /// <summary>
    /// False when the browser cannot record at all — no AudioWorklet, or an insecure origin.
    /// getUserMedia needs a secure context, which localhost satisfies but plain HTTP does not.
    /// </summary>
    public async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>("pochopaudio.recorder.isSupported");
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <summary>Opens the microphone and begins capturing. Throws if permission is refused.</summary>
    public async Task StartAsync(Action<RecordingLevel> onLevel, Action onLimitReached)
    {
        _onLevel = onLevel;
        _onLimitReached = onLimitReached;
        _self ??= DotNetObjectReference.Create(this);

        await js.InvokeVoidAsync("pochopaudio.recorder.start", _self, MaxRecordingBytes);
        IsRecording = true;
    }

    /// <summary>Stops capture and returns the WAV, or null if nothing was captured.</summary>
    public async Task<RecordingResult?> StopAsync()
    {
        if (!IsRecording)
        {
            return null;
        }

        IsRecording = false;

        try
        {
            return await js.InvokeAsync<RecordingResult?>("pochopaudio.recorder.stop");
        }
        finally
        {
            _onLevel = null;
            _onLimitReached = null;
        }
    }

    [JSInvokable]
    public void OnLevel(double peakDb, double elapsedSeconds, bool clipped) =>
        _onLevel?.Invoke(new RecordingLevel(peakDb, elapsedSeconds, clipped));

    [JSInvokable]
    public void OnLimitReached() => _onLimitReached?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (IsRecording)
        {
            try
            {
                await StopAsync();
            }
            catch (JSException)
            {
                // Navigating away mid-recording; the page teardown releases the microphone anyway.
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
        _self = null;
    }
}
