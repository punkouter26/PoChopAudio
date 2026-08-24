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

public partial class CutoutViewModel : ObservableObject, IDisposable
{
    private readonly CutoutApiClient _apiClient;
    private readonly Debouncer _debouncer = new(TimeSpan.FromMilliseconds(350));
    private CancellationTokenSource _cts = new();

    public CutoutViewModel(CutoutApiClient apiClient)
    {
        _apiClient = apiClient;

        BatchKnobs.PropertyChanged += (s, e) =>
        {
            _debouncer.Debounce(async () => await ReprocessAllAutoAsync());
        };
    }

    [ObservableProperty]
    private ObservableCollection<CutoutFileItem> _files = [];

    [ObservableProperty]
    private CutoutKnobsModel _batchKnobs = new();

    [ObservableProperty]
    private ObservableCollection<BatchEntry> _recentBatches = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _filenameTemplate = "{stem}_cutout.png";

    [ObservableProperty]
    private CutoutCapabilities? _capabilities;

    public int ReadyCount => Files.Count(f => f.IsReady);

    public async Task InitializeAsync()
    {
        Capabilities = await _apiClient.GetCapabilitiesAsync();
        await LoadRecentBatchesAsync();
    }

    [RelayCommand]
    public async Task PickFilesAsync(Window window)
    {
        ErrorMessage = null;
        var picker = new FileOpenPicker();
        WindowHelper.InitializeWithWindow(picker, window);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

        var exts = Capabilities?.SupportedExtensions ?? [".jpg", ".jpeg", ".png", ".webp"];
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
        var available = CutoutLimits.MaxBatchFiles - Files.Count;
        if (available <= 0)
        {
            ErrorMessage = $"Batch limit reached (maximum {CutoutLimits.MaxBatchFiles} images allowed).";
            return;
        }

        var toAdd = list.Take(available).ToList();
        var newItems = new List<CutoutFileItem>();

        foreach (var path in toAdd)
        {
            var fileInfo = new FileInfo(path);
            var item = new CutoutFileItem
            {
                FileName = fileInfo.Name,
                LocalFilePath = path,
                Bytes = fileInfo.Exists ? fileInfo.Length : 0,
                Settings = BatchKnobs.Clone(),
                Status = ItemProcessingStatus.Queued
            };

            if (fileInfo.Exists)
            {
                try
                {
                    var bmp = new BitmapImage(new Uri(path));
                    item.OriginalImage = bmp;
                }
                catch
                {
                    // Ignore local thumbnail load failure
                }
            }

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
                StatusMessage = $"Removing background from {item.FileName} ({i + 1} of {newItems.Count})…";

                if (File.Exists(item.LocalFilePath))
                {
                    await using var fs = File.OpenRead(item.LocalFilePath);
                    await UploadAndProcessItemAsync(item, fs, item.FileName);
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

    private async Task UploadAndProcessItemAsync(CutoutFileItem item, Stream stream, string fileName)
    {
        try
        {
            item.Status = ItemProcessingStatus.Uploading;
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png"
            };

            var uploadResult = await _apiClient.UploadAsync(stream, fileName, contentType, _cts.Token);
            item.JobId = uploadResult.JobId;
            item.Width = uploadResult.Width;
            item.Height = uploadResult.Height;

            item.Status = ItemProcessingStatus.Analyzing;
            var result = await _apiClient.AnalyzeAsync(item.JobId, item.Settings.ToOptions(), _cts.Token);
            item.Warning = result.Warning;

            // Fetch transparent cutout png
            var pngBytes = await _apiClient.GetCutoutImageAsync(item.JobId, _cts.Token);
            await SetCutoutImageAsync(item, pngBytes);

            item.Status = ItemProcessingStatus.Ready;
        }
        catch (Exception ex)
        {
            item.Status = ItemProcessingStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
    }

    private static async Task SetCutoutImageAsync(CutoutFileItem item, byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var randomAccess = ms.AsRandomAccessStream();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(randomAccess);
        item.CutoutImage = bmp;
    }

    private async Task ReprocessAllAutoAsync()
    {
        var targets = Files.Where(f => f.IsReady).ToList();
        if (targets.Count == 0) return;

        foreach (var item in targets)
        {
            item.Settings = BatchKnobs.Clone();
            await ReprocessOneAsync(item);
        }
    }

    [RelayCommand]
    public async Task ReprocessAllAsync()
    {
        var targets = Files.Where(f => !string.IsNullOrEmpty(f.JobId)).ToList();
        if (targets.Count == 0) return;

        IsBusy = true;
        StatusMessage = "Re-processing cutouts…";
        try
        {
            foreach (var item in targets)
            {
                item.Settings = BatchKnobs.Clone();
                await ReprocessOneAsync(item);
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
    public async Task ReprocessOneAsync(CutoutFileItem item)
    {
        if (string.IsNullOrEmpty(item.JobId)) return;

        try
        {
            item.Status = ItemProcessingStatus.Analyzing;
            var result = await _apiClient.AnalyzeAsync(item.JobId, item.Settings.ToOptions(), _cts.Token);
            item.Warning = result.Warning;

            var pngBytes = await _apiClient.GetCutoutImageAsync(item.JobId, _cts.Token);
            await SetCutoutImageAsync(item, pngBytes);

            item.Status = ItemProcessingStatus.Ready;
            NotifyCountProperties();
        }
        catch (Exception ex)
        {
            item.Status = ItemProcessingStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task ExportBatchZipAsync(Window window)
    {
        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var savePath = await ExportService.PickSaveFileAsync(window, "cutouts.zip", ".zip", "ZIP Archive");
        if (string.IsNullOrEmpty(savePath)) return;

        IsBusy = true;
        StatusMessage = "Creating cutouts ZIP…";

        try
        {
            var jobIds = ready.Select(f => f.JobId).ToList();
            await using var stream = await _apiClient.GetBatchZipStreamAsync(jobIds, FilenameTemplate, _cts.Token);
            await ExportService.SaveStreamToFileAsync(stream, savePath, _cts.Token);
            StatusMessage = $"Saved ZIP to {Path.GetFileName(savePath)}";
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
            for (int i = 0; i < ready.Count; i++)
            {
                var item = ready[i];
                var stem = Path.GetFileNameWithoutExtension(item.FileName);
                StatusMessage = $"Saving {stem}_cutout.png ({i + 1} of {ready.Count})…";

                var pngBytes = await _apiClient.GetCutoutImageAsync(item.JobId, _cts.Token);
                var targetFile = Path.Combine(folderPath, $"{stem}_cutout.png");
                await ExportService.SaveBytesToFileAsync(pngBytes, targetFile, _cts.Token);
            }
            StatusMessage = $"Exported {ready.Count} images to {folderPath}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save images: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveSingleCutoutAsync((CutoutFileItem Item, Window Window) args)
    {
        var stem = Path.GetFileNameWithoutExtension(args.Item.FileName);
        var savePath = await ExportService.PickSaveFileAsync(args.Window, $"{stem}_cutout.png", ".png", "PNG Image");
        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            var bytes = await _apiClient.GetCutoutImageAsync(args.Item.JobId, _cts.Token);
            await ExportService.SaveBytesToFileAsync(bytes, savePath, _cts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save image: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        Files.Clear();
        ErrorMessage = null;
        StatusMessage = string.Empty;
        NotifyCountProperties();
    }

    [RelayCommand]
    public async Task LoadRecentBatchesAsync()
    {
        var list = await _apiClient.ListBatchesAsync(_cts.Token);
        RecentBatches.Clear();
        if (list is not null)
        {
            foreach (var b in list)
            {
                RecentBatches.Add(b);
            }
        }
    }

    [RelayCommand]
    public async Task DeleteRecentBatchAsync(string batchId)
    {
        await _apiClient.DeleteBatchAsync(batchId, _cts.Token);
        await LoadRecentBatchesAsync();
    }

    private void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(ReadyCount));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _debouncer.Dispose();
    }
}

