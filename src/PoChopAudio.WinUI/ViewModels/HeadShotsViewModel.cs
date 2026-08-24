using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;

namespace PoChopAudio.WinUI.ViewModels;

public partial class HeadShotsViewModel : ObservableObject, IDisposable
{
    private readonly CameraService _camera;
    private readonly LocalCutoutService _localCutout;
    private CancellationTokenSource _cts = new();

    public HeadShotsViewModel(CameraService camera, LocalCutoutService localCutout)
    {
        _camera = camera;
        _localCutout = localCutout;
    }

    [ObservableProperty]
    private ObservableCollection<HeadShotItem> _headShots = [];

    [ObservableProperty]
    private bool _isCameraRunning;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isBurstMode = true;

    [ObservableProperty]
    private int _shotCount = 5;

    [ObservableProperty]
    private int _countdown;

    [ObservableProperty]
    private string _headShotName = "Head";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public CameraService Camera => _camera;

    [RelayCommand]
    public async Task StartCameraAsync()
    {
        ErrorMessage = null;
        var ok = await _camera.InitializeAsync();
        if (ok)
        {
            IsCameraRunning = true;
            StatusMessage = "Camera ready. Align your face inside the guide.";
        }
        else
        {
            ErrorMessage = "Could not initialize camera. Ensure a webcam is connected and permitted.";
        }
    }

    [RelayCommand]
    public void StopCamera()
    {
        _camera.Dispose();
        IsCameraRunning = false;
        StatusMessage = "Camera stopped.";
    }

    [RelayCommand]
    public async Task ShootAsync()
    {
        if (IsCapturing) return;

        if (!IsCameraRunning)
        {
            await StartCameraAsync();
            if (!IsCameraRunning) return;
        }

        IsCapturing = true;
        ErrorMessage = null;

        try
        {
            int total = IsBurstMode ? ShotCount : 1;
            var baseName = string.IsNullOrWhiteSpace(HeadShotName) ? "Head" : HeadShotName.Trim();

            for (int i = 0; i < total; i++)
            {
                if (_cts.IsCancellationRequested) break;

                // Countdown for each shot
                for (int c = 3; c > 0; c--)
                {
                    Countdown = c;
                    await Task.Delay(1000);
                }
                Countdown = 0;

                StatusMessage = $"Shooting {i + 1} of {total}…";
                var frameBytes = await _camera.CapturePhotoAsync();
                if (frameBytes is null || frameBytes.Length == 0) continue;

                var item = new HeadShotItem
                {
                    Name = $"{baseName}_{HeadShots.Count + 1}",
                    Index = HeadShots.Count + 1,
                    CapturedAt = DateTimeOffset.Now,
                    IsProcessing = true
                };
                HeadShots.Add(item);

                // Run local on-device background removal and head crop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (pngBytes, w, h) = await _localCutout.ProcessHeadshotAsync(frameBytes, _cts.Token);
                        item.CutoutPngBytes = pngBytes;
                        item.Width = w;
                        item.Height = h;

                        using var ms = new MemoryStream(pngBytes);
                        var ras = ms.AsRandomAccessStream();
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(ras);
                        item.Image = bmp;
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                    }
                    finally
                    {
                        item.IsProcessing = false;
                    }
                });

                if (i < total - 1)
                {
                    await Task.Delay(1000);
                }
            }

            StatusMessage = $"Captured {total} head shot(s).";
        }
        finally
        {
            IsCapturing = false;
            Countdown = 0;
        }
    }

    [RelayCommand]
    public void DeleteHeadShot(HeadShotItem item)
    {
        HeadShots.Remove(item);
        // Re-number
        for (int i = 0; i < HeadShots.Count; i++)
        {
            HeadShots[i].Index = i + 1;
        }
    }

    [RelayCommand]
    public async Task SaveHeadShotAsync((HeadShotItem Item, Window Window) args)
    {
        var savePath = await ExportService.PickSaveFileAsync(args.Window, $"{args.Item.Name}.png", ".png", "PNG Image");
        if (string.IsNullOrEmpty(savePath) || args.Item.CutoutPngBytes.Length == 0) return;

        try
        {
            await ExportService.SaveBytesToFileAsync(args.Item.CutoutPngBytes, savePath, _cts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save headshot: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveAllToFolderAsync(Window window)
    {
        var ready = HeadShots.Where(h => h.CutoutPngBytes.Length > 0).ToList();
        if (ready.Count == 0) return;

        var folderPath = await ExportService.PickFolderAsync(window);
        if (string.IsNullOrEmpty(folderPath)) return;

        try
        {
            for (int i = 0; i < ready.Count; i++)
            {
                var item = ready[i];
                var targetFile = Path.Combine(folderPath, $"{item.Name}.png");
                await ExportService.SaveBytesToFileAsync(item.CutoutPngBytes, targetFile, _cts.Token);
            }
            StatusMessage = $"Saved {ready.Count} head shots to {folderPath}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save headshots: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        HeadShots.Clear();
        ErrorMessage = null;
        StatusMessage = string.Empty;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _camera.Dispose();
        _localCutout.Dispose();
    }
}

