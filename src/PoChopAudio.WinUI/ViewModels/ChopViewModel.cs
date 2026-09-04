using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Dsp;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using Windows.Storage.Pickers;

namespace PoChopAudio.WinUI.ViewModels;

public partial class ChopViewModel : ObservableObject, IDisposable
{
    private readonly ChopService _chop;
    private readonly AudioRecorderService _recorder;
    private readonly AudioPlayerService _player;
    private readonly AppSettingsService _settings;
    private readonly AudioCueService _cues;
    private readonly Debouncer _debouncer = new(TimeSpan.FromMilliseconds(350));
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource _cts = new();

    public ChopViewModel(
        ChopService chop,
        AudioRecorderService recorder,
        AudioPlayerService player,
        AppSettingsService settings,
        AudioCueService cues)
    {
        _chop = chop;
        _recorder = recorder;
        _player = player;
        _settings = settings;
        _cues = cues;
        _cues.IsEnabled = settings.Current.CueSoundsEnabled;
        settings.Changed += s => _cues.IsEnabled = s.CueSoundsEnabled;

        // Every callback below arrives on a background thread -- LevelUpdated on NAudio's capture
        // thread, ElapsedUpdated on a System.Timers.Timer thread, and the player's on its own.
        // Setting observable properties there raises PropertyChanged off the UI thread, which the
        // XAML bindings cannot act on: the level meter stayed at -inf dB and the elapsed clock sat
        // at 00:00 for the whole take. Marshal first, then set.
        _recorder.LevelUpdated += (peak, rms, clip) => OnUiThread(() =>
        {
            PeakDb = peak;
            RmsDb = rms;
            IsClipping = clip;
        });

        _recorder.ElapsedUpdated += elapsed => OnUiThread(() =>
        {
            RecordingElapsed = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        });

        _player.PositionUpdated += (curr, total) => OnUiThread(() =>
        {
            if (ActivePlayingItem is not null && total > 0)
            {
                ActivePlayingItem.PlayheadRatio = curr / total;
            }
        });

        _player.PlaybackStopped += () => OnUiThread(() =>
        {
            if (ActivePlayingItem is not null)
            {
                // The closing blip sounds here, once the clip has finished, so it marks the end
                // rather than covering the tail of what was being judged.
                var wasClip = ActivePlayingItem.PlayingSegmentIndex is not null;

                ActivePlayingItem.IsPlaying = false;
                ActivePlayingItem.PlayheadRatio = 0;
                ActivePlayingItem.PlayingSegmentIndex = null;
                ActivePlayingItem = null;

                if (wasClip)
                {
                    _cues.Play(AudioCue.ClipEnd);
                }
            }
        });

        // Listen for batch knob changes
        BatchKnobs.PropertyChanged += (s, e) =>
        {
            _debouncer.Debounce(async () => await ResplitBatchAutoAsync());
        };

        Files.CollectionChanged += (s, e) => NotifyCountProperties();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, inline when already there.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    public void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FilesSummaryText));
        OnPropertyChanged(nameof(TotalClips));
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(UntunedCount));
    }

    [ObservableProperty]
    private ObservableCollection<ChopFileItem> _files = [];

    [ObservableProperty]
    private ChopKnobsModel _batchKnobs = new();

    [ObservableProperty]
    private ExportKnobsModel _exportKnobs = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(NeedsRecordingName))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(NeedsRecordingName))]
    private string _recordingName = string.Empty;

    [ObservableProperty]
    private string _recordingElapsed = "00:00";

    [ObservableProperty]
    private int _countdown;

    [ObservableProperty]
    private double _peakDb = -100;

    [ObservableProperty]
    private double _rmsDb = -100;

    [ObservableProperty]
    private bool _isClipping;

    [ObservableProperty]
    private ChopFileItem? _activePlayingItem;

    [ObservableProperty]
    private ChopCapabilities? _capabilities;

    public bool HasFiles => Files.Count > 0;
    public string FilesSummaryText => $"{Files.Count} files · {TotalClips} clips";
    public int TotalClips => Files.Sum(f => f.Segments.Count);
    public int ReadyCount => Files.Count(f => f.IsReady);
    public int AttentionCount => Files.Count(f => f.NeedsAttention);
    public int UntunedCount => Files.Count(f => !f.UsesOwnSettings && f.IsReady);

    public Task InitializeAsync()
    {
        Capabilities = _chop.GetCapabilities();
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task PickFilesAsync(Window window)
    {
        ErrorMessage = null;
        var picker = new FileOpenPicker();
        WindowHelper.InitWithWindow(picker, window);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;

        var exts = Capabilities?.SupportedExtensions ?? [".wav", ".mp3", ".aiff", ".aif"];
        foreach (var ext in exts)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var picked = await picker.PickMultipleFilesAsync();
        if (picked is not null && picked.Count > 0)
        {
            var paths = picked.Select(p => p.Path).ToList();
            await AddFilesAsync(paths);
        }
    }

    public async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        ErrorMessage = null;
        var list = filePaths.ToList();
        var available = ChopLimits.MaxBatchFiles - Files.Count;
        if (available <= 0)
        {
            ErrorMessage = $"Batch limit reached (maximum {ChopLimits.MaxBatchFiles} files allowed).";
            return;
        }

        var toAdd = list.Take(available).ToList();
        var newItems = new List<ChopFileItem>();

        foreach (var path in toAdd)
        {
            var fileInfo = new FileInfo(path);
            var item = new ChopFileItem
            {
                FileName = fileInfo.Name,
                LocalFilePath = path,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                Settings = BatchKnobs.Clone(),
                IsExpanded = Files.Count == 0 && toAdd.Count == 1,
                Status = ItemProcessingStatus.Queued
            };
            Files.Add(item);
            newItems.Add(item);
        }

        NotifyCountProperties();
        IsBusy = true;

        try
        {
            for (int i = 0; i < newItems.Count; i++)
            {
                var item = newItems[i];
                StatusMessage = $"Decoding {item.FileName} ({i + 1} of {newItems.Count})…";

                if (File.Exists(item.LocalFilePath))
                {
                    await using var fs = File.OpenRead(item.LocalFilePath);
                    await UploadAndAnalyzeItemAsync(item, fs, item.FileName);
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }
    }

    private async Task UploadAndAnalyzeItemAsync(ChopFileItem item, Stream stream, string fileName)
    {
        try
        {
            item.Status = ItemProcessingStatus.Uploading;
            var uploadResult = (await _chop.UploadAsync(stream, fileName, stream.Length, _cts.Token)).OrThrow();

            item.JobId = uploadResult.JobId;
            item.DurationSeconds = uploadResult.DurationSeconds;
            item.SampleRate = uploadResult.SampleRate;
            item.Channels = uploadResult.Channels;
            item.PeakDb = uploadResult.PeakDb;
            item.NoiseFloorDb = uploadResult.NoiseFloorDb;
            item.Waveform = uploadResult.Waveform;

            item.Status = ItemProcessingStatus.Analyzing;
            var analysis = await Task.Run(
                () => _chop.Analyze(item.JobId, item.Settings.ToOptions()).OrThrow(), _cts.Token);

            item.DetectedThresholdDb = analysis.ThresholdDb;
            item.Warning = analysis.Warning;
            item.Segments = new ObservableCollection<ChopSegment>(analysis.Segments);
            item.Status = ItemProcessingStatus.Ready;
        }
        catch (Exception ex)
        {
            item.Status = ItemProcessingStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// A take has to be named before it can be recorded. The name becomes the WAV's filename and
    /// every clip stem chopped out of it, so an unnamed take lands as an opaque Take_[timestamp]
    /// that is impossible to tell apart from the next one in a batch.
    /// </summary>
    public bool CanStartRecording => !IsRecording && !string.IsNullOrWhiteSpace(RecordingName);

    /// <summary>True only when the missing name is what is holding recording up, so the hint does
    /// not stay on screen while a take is already running.</summary>
    public bool NeedsRecordingName => !IsRecording && string.IsNullOrWhiteSpace(RecordingName);

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    public async Task StartRecordingAsync()
    {
        if (!CanStartRecording) return;

        ErrorMessage = null;

        // The audible count-in is rendered as one buffer and started here, so its beats cannot
        // drift; the visible numbers are then stepped alongside it. When cue sounds are off this
        // degrades to exactly the silent countdown it replaced.
        const int beats = 3;
        const double bpm = 60;
        _cues.PlayCountIn(beats, bpm);

        for (var c = beats; c > 0; c--)
        {
            Countdown = c;
            await Task.Delay(TimeSpan.FromSeconds(60.0 / bpm));
        }

        Countdown = 0;

        // Nothing this class can make a sound with may do so from here until Stop: a cue that
        // leaks into a take does not annoy the user, it corrupts their recording.
        _cues.IsSuppressed = true;
        _recorder.Start();
        IsRecording = true;
    }

    [RelayCommand]
    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        IsRecording = false;
        var wavBytes = _recorder.Stop();
        _cues.IsSuppressed = false;

        if (wavBytes.Length == 0) return;

        var stem = string.IsNullOrWhiteSpace(RecordingName)
            ? $"Take_{DateTime.Now:yyyyMMdd_HHmmss}"
            : RecordingName.Trim();

        var fileName = $"{stem}.wav";
        var item = new ChopFileItem
        {
            FileName = fileName,
            SizeBytes = wavBytes.Length,
            Settings = BatchKnobs.Clone(),
            IsExpanded = true,
            Status = ItemProcessingStatus.Queued
        };

        Files.Add(item);
        NotifyCountProperties();

        IsBusy = true;
        StatusMessage = $"Processing recording {fileName}…";

        try
        {
            using var ms = new MemoryStream(wavBytes);
            await UploadAndAnalyzeItemAsync(item, ms, fileName);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }

        _cues.Play(item.IsReady ? AudioCue.Success : AudioCue.Failure);
        BatchCompleted?.Invoke(item.IsReady);
    }

    /// <summary>
    /// Raised when a batch finishes, with whether it worked. The page uses it to fire the
    /// celebration burst; the view model deliberately knows nothing about particles.
    /// </summary>
    public event Action<bool>? BatchCompleted;

    /// <summary>
    /// Shows or hides the frequency view for one file, building it on first use.
    ///
    /// <para>
    /// Built on demand rather than during analysis: it reads the whole canonical WAV back off disk
    /// and runs a few hundred FFTs, which is not work to do for every file in a batch on the chance
    /// that someone looks at one of them.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task ToggleSpectrogramAsync(ChopFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ShowSpectrogram)
        {
            item.ShowSpectrogram = false;
            return;
        }

        if (item.Spectrogram is null)
        {
            item.IsBuildingSpectrogram = true;

            try
            {
                var outcome = await Task.Run(
                    () => _chop.GetSpectrogram(item.JobId, columns: 720, bins: 128), _cts.Token);

                if (!outcome.IsSuccess)
                {
                    ErrorMessage = outcome.Message;
                    return;
                }

                item.Spectrogram = outcome.Value;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = $"Could not build the frequency view: {exception.Message}";
                return;
            }
            finally
            {
                item.IsBuildingSpectrogram = false;
            }
        }

        item.ShowSpectrogram = true;
    }

    /// <summary>Moves playback to a position dragged on the waveform.</summary>
    public void Seek(ChopFileItem item, double seconds)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (ActivePlayingItem == item && _player.IsPlaying)
        {
            _player.Seek(seconds);
        }
    }

    /// <summary>
    /// Plays a 1 kHz tone at -18 dBFS so the input meter can be read against a known level before a
    /// take rather than after one.
    /// </summary>
    [RelayCommand]
    public void PlayReferenceTone()
    {
        if (IsRecording)
        {
            return;
        }

        _cues.PlayReferenceTone();
        StatusMessage = "Playing a 1 kHz reference tone at -18 dBFS.";
    }

    private async Task ResplitBatchAutoAsync()
    {
        var targets = Files.Where(f => !f.UsesOwnSettings && f.IsReady).ToList();
        if (targets.Count == 0) return;

        foreach (var item in targets)
        {
            item.Settings.LoadFrom(BatchKnobs.ToOptions());
            await ResplitOneAsync(item);
        }
    }

    [RelayCommand]
    public async Task ResplitAllAsync()
    {
        var targets = Files.Where(f => !f.UsesOwnSettings && f.IsReady).ToList();
        if (targets.Count == 0) return;

        IsBusy = true;
        StatusMessage = "Re-splitting recordings…";
        try
        {
            foreach (var item in targets)
            {
                item.Settings.LoadFrom(BatchKnobs.ToOptions());
                await ResplitOneAsync(item);
            }
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }
    }

    [RelayCommand]
    public async Task ResplitOneAsync(ChopFileItem item)
    {
        if (string.IsNullOrEmpty(item.JobId)) return;

        try
        {
            var result = await Task.Run(
                () => _chop.Analyze(item.JobId, item.Settings.ToOptions()).OrThrow(), _cts.Token);
            item.DetectedThresholdDb = result.ThresholdDb;
            item.Warning = result.Warning;
            item.Segments = new ObservableCollection<ChopSegment>(result.Segments);
            item.Version++;
            NotifyCountProperties();
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public void FollowBatch(ChopFileItem item)
    {
        item.UsesOwnSettings = false;
        item.Settings.LoadFrom(BatchKnobs.ToOptions());
        _ = ResplitOneAsync(item);
    }

    public async Task PlayTakeAsync(ChopFileItem item, ChopSegment? segment)
    {
        if (item.IsPlaying && item.PlayingSegmentIndex == segment?.Index)
        {
            _player.Stop();
            return;
        }

        try
        {
            byte[] wavBytes;
            if (segment is not null)
            {
                wavBytes = await Task.Run(
                    () => _chop.GetClip(item.JobId, segment.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
            }
            else
            {
                // Play entire audio if local file exists, or fetch take 1
                if (!string.IsNullOrEmpty(item.LocalFilePath) && File.Exists(item.LocalFilePath))
                {
                    wavBytes = await File.ReadAllBytesAsync(item.LocalFilePath, _cts.Token);
                }
                else if (item.Segments.Count > 0)
                {
                    wavBytes = await Task.Run(
                        () => _chop.GetClip(item.JobId, item.Segments[0].Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
                }
                else
                {
                    return;
                }
            }

            if (ActivePlayingItem is not null && ActivePlayingItem != item)
            {
                ActivePlayingItem.IsPlaying = false;
                ActivePlayingItem.PlayingSegmentIndex = null;
            }

            ActivePlayingItem = item;
            item.IsPlaying = true;
            item.PlayingSegmentIndex = segment?.Index;

            // Brackets the clip rather than playing over it: the blip finishes before the audio
            // starts, and its partner sounds only once playback has stopped.
            if (segment is not null)
            {
                _cues.Play(AudioCue.ClipStart);
            }

            _player.Play(wavBytes);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not play take: {ex.Message}";
        }
    }

    [RelayCommand]
    public void StopPlayback()
    {
        _player.Stop();
    }

    [RelayCommand]
    public async Task ExportBatchZipAsync(Window window)
    {
        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var savePath = await ExportService.PickSaveFileAsync(window, "clips.zip", ".zip", "ZIP Archive");
        if (string.IsNullOrEmpty(savePath)) return;

        IsBusy = true;
        StatusMessage = "Creating batch ZIP…";

        try
        {
            var jobIds = ready.Select(f => f.JobId).ToList();
            var zip = await Task.Run(
                () => _chop.GetBatchZip(jobIds, ExportKnobs.ToOptions()).OrThrow(), _cts.Token);
            await ExportService.SaveBytesToFileAsync(zip.Content, savePath, _cts.Token);
            StatusMessage = $"Saved batch ZIP to {Path.GetFileName(savePath)}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to export ZIP: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveAllToFolderAsync(Window window)
    {
        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var folderPath = await ExportService.ResolveBatchFolderAsync(window, _settings);
        if (string.IsNullOrEmpty(folderPath)) return;

        IsBusy = true;
        try
        {
            int totalSaved = 0;
            for (int f = 0; f < ready.Count; f++)
            {
                var fileItem = ready[f];
                var stem = Path.GetFileNameWithoutExtension(fileItem.FileName);

                for (int s = 0; s < fileItem.Segments.Count; s++)
                {
                    var seg = fileItem.Segments[s];
                    StatusMessage = $"Saving {stem}_{seg.Index}.wav ({totalSaved + 1} of {TotalClips})…";

                    var clipBytes = await Task.Run(
                        () => _chop.GetClip(fileItem.JobId, seg.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
                    var targetFile = Path.Combine(folderPath, $"{stem}_{seg.Index}.wav");
                    await ExportService.SaveBytesToFileAsync(clipBytes, targetFile, _cts.Token);
                    totalSaved++;
                }
            }
            StatusMessage = $"Exported {totalSaved} clips to {folderPath}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save clips to folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveTakeClipAsync((ChopFileItem Item, ChopSegment Segment, Window Window) args)
    {
        var stem = Path.GetFileNameWithoutExtension(args.Item.FileName);
        var defaultName = $"{stem}_{args.Segment.Index}.wav";
        var savePath = await ExportService.PickSaveFileAsync(args.Window, defaultName, ".wav", "WAV Audio");
        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            var bytes = await Task.Run(
                () => _chop.GetClip(args.Item.JobId, args.Segment.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
            await ExportService.SaveBytesToFileAsync(bytes, savePath, _cts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save clip: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        _player.Stop();
        foreach (var file in Files)
        {
            _chop.Delete(file.JobId);
        }
        Files.Clear();
        ErrorMessage = null;
        StatusMessage = string.Empty;
        NotifyCountProperties();
    }

    [RelayCommand]
    public void ResetSettings()
    {
        BatchKnobs.LoadFrom(new ChopOptions());
        ExportKnobs = new ExportKnobsModel();
    }

    /// <summary>
    /// Returns the page to its opening state: drops every loaded file and job, resets both knob
    /// sets, and clears the pending take name. ClearAll on its own leaves the tuned knobs and the
    /// typed filename behind, which is not what "start over" means to someone staring at a bad run.
    /// </summary>
    [RelayCommand]
    public void StartOver()
    {
        if (IsRecording)
        {
            // Abandon rather than finish: StopRecordingAsync would save the take we are about to
            // discard. Stop() still has to run so the capture device is released.
            IsRecording = false;
            _ = _recorder.Stop();
        }

        ClearAll();
        ResetSettings();
        RecordingName = string.Empty;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _debouncer.Dispose();
        _recorder.Dispose();
        _player.Dispose();
    }
}

