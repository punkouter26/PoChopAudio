using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using Windows.Storage.Pickers;

namespace PoChopAudio.WinUI.ViewModels;

public partial class ChopViewModel : ObservableObject, IDisposable
{
    private readonly ChopApiClient _apiClient;
    private readonly AudioRecorderService _recorder;
    private readonly AudioPlayerService _player;
    private readonly Debouncer _debouncer = new(TimeSpan.FromMilliseconds(350));
    private CancellationTokenSource _cts = new();

    public ChopViewModel(ChopApiClient apiClient, AudioRecorderService recorder, AudioPlayerService player)
    {
        _apiClient = apiClient;
        _recorder = recorder;
        _player = player;

        _recorder.LevelUpdated += (peak, rms, clip) =>
        {
            PeakDb = peak;
            RmsDb = rms;
            IsClipping = clip;
        };

        _recorder.ElapsedUpdated += elapsed =>
        {
            RecordingElapsed = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        };

        _player.PositionUpdated += (curr, total) =>
        {
            if (ActivePlayingItem is not null && total > 0)
            {
                ActivePlayingItem.PlayheadRatio = curr / total;
            }
        };

        _player.PlaybackStopped += () =>
        {
            if (ActivePlayingItem is not null)
            {
                ActivePlayingItem.IsPlaying = false;
                ActivePlayingItem.PlayheadRatio = 0;
                ActivePlayingItem.PlayingSegmentIndex = null;
                ActivePlayingItem = null;
            }
        };

        // Listen for batch knob changes
        BatchKnobs.PropertyChanged += (s, e) =>
        {
            _debouncer.Debounce(async () => await ResplitBatchAutoAsync());
        };
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
    private bool _isRecording;

    [ObservableProperty]
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

    public int TotalClips => Files.Sum(f => f.Segments.Count);
    public int ReadyCount => Files.Count(f => f.IsReady);
    public int AttentionCount => Files.Count(f => f.NeedsAttention);
    public int UntunedCount => Files.Count(f => !f.UsesOwnSettings && f.IsReady);

    public async Task InitializeAsync()
    {
        Capabilities = await _apiClient.GetCapabilitiesAsync();
    }

    [RelayCommand]
    public async Task PickFilesAsync(Window window)
    {
        ErrorMessage = null;
        var picker = new FileOpenPicker();
        WindowHelper.InitializeWithWindow(picker, window);
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
            var uploadResult = await _apiClient.UploadAsync(stream, fileName, _cts.Token);

            item.JobId = uploadResult.JobId;
            item.DurationSeconds = uploadResult.DurationSeconds;
            item.SampleRate = uploadResult.SampleRate;
            item.Channels = uploadResult.Channels;
            item.PeakDb = uploadResult.PeakDb;
            item.NoiseFloorDb = uploadResult.NoiseFloorDb;
            item.Waveform = uploadResult.Waveform;

            item.Status = ItemProcessingStatus.Analyzing;
            var analysis = await _apiClient.AnalyzeAsync(item.JobId, item.Settings.ToOptions(), _cts.Token);

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

    [RelayCommand]
    public async Task StartRecordingAsync()
    {
        if (IsRecording) return;

        ErrorMessage = null;
        for (int c = 3; c > 0; c--)
        {
            Countdown = c;
            await Task.Delay(1000);
        }
        Countdown = 0;

        _recorder.Start();
        IsRecording = true;
    }

    [RelayCommand]
    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        IsRecording = false;
        var wavBytes = _recorder.Stop();
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
            var result = await _apiClient.AnalyzeAsync(item.JobId, item.Settings.ToOptions(), _cts.Token);
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

    [RelayCommand]
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
                wavBytes = await _apiClient.GetClipAudioAsync(item.JobId, segment.Index, ExportKnobs.ToOptions(), _cts.Token);
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
                    wavBytes = await _apiClient.GetClipAudioAsync(item.JobId, item.Segments[0].Index, ExportKnobs.ToOptions(), _cts.Token);
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
            await using var stream = await _apiClient.GetBatchZipStreamAsync(jobIds, ExportKnobs.ToOptions(), _cts.Token);
            await ExportService.SaveStreamToFileAsync(stream, savePath, _cts.Token);
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

        var folderPath = await ExportService.PickFolderAsync(window);
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

                    var clipBytes = await _apiClient.GetClipAudioAsync(fileItem.JobId, seg.Index, ExportKnobs.ToOptions(), _cts.Token);
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
            var bytes = await _apiClient.GetClipAudioAsync(args.Item.JobId, args.Segment.Index, ExportKnobs.ToOptions(), _cts.Token);
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
            _ = _apiClient.DeleteJobAsync(file.JobId);
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

    private void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(TotalClips));
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(UntunedCount));
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

