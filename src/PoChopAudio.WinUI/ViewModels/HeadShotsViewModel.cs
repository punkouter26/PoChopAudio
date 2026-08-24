using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;

namespace PoChopAudio.WinUI.ViewModels;

public partial class HeadShotsViewModel : ObservableObject
{
    private readonly CameraService _camera;
    private readonly LocalCutoutService _localCutout;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<HeadShotItem> HeadShots { get; } = [];

    [ObservableProperty]
    private bool _isCameraRunning;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isBurstMode = true;

    [ObservableProperty]
    private double _shotCount = 5;

    [ObservableProperty]
    private int _countdown;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    private bool _hasCountdown;

    [ObservableProperty]
    private string _headShotName = "Head";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasHeadShots => HeadShots.Count > 0;
    public CameraService Camera => _camera;

    public HeadShotsViewModel(CameraService camera, LocalCutoutService localCutout)
    {
        _camera = camera;
        _localCutout = localCutout;
        HeadShots.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasHeadShots));
    }

    partial void OnCountdownChanged(int value)
    {
        HasCountdown = value > 0;
        CountdownText = value > 0 ? value.ToString() : string.Empty;
    }

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
            ErrorMessage = "Could not initialize camera. Please check camera permissions.";
        }
    }

    [RelayCommand]
    public void StopCamera()
    {
        _camera.Dispose();
        IsCameraRunning = false;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    public async Task ShootAsync()
    {
        if (!IsCameraRunning || IsCapturing) return;

        IsCapturing = true;
        ErrorMessage = null;

        try
        {
            int total = IsBurstMode ? Math.Clamp((int)ShotCount, 1, 20) : 1;

            for (int i = 0; i < total; i++)
            {
                if (IsBurstMode)
                {
                    // 3, 2, 1 Countdown
                    for (int c = 3; c >= 1; c--)
                    {
                        Countdown = c;
                        await Task.Delay(1000);
                    }
                    Countdown = 0;
                }

                StatusMessage = $"Shooting {i + 1} of {total}…";
                var photoBytes = await _camera.CapturePhotoAsync();
                if (photoBytes is null || photoBytes.Length == 0)
                {
                    ErrorMessage = "Failed to capture frame from camera.";
                    break;
                }

                var item = new HeadShotItem
                {
                    Index = HeadShots.Count + 1,
                    Name = $"{HeadShotName}_{HeadShots.Count + 1}",
                    OriginalJpegBytes = photoBytes,
                    IsProcessing = true
                };

                // Create original image preview
                using var ms = new MemoryStream(photoBytes);
                var ras = ms.AsRandomAccessStream();
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ras);
                item.Image = bmp;

                HeadShots.Add(item);

                // Run AI cutout in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cutoutBytes = await _localCutout.RemoveBackgroundAndCropHeadAsync(photoBytes, _cts.Token);
                        item.CutoutPngBytes = cutoutBytes;

                        // Get dimensions using ImageSharp metadata
                        using var img = SixLabors.ImageSharp.Image.Load(cutoutBytes);
                        item.Width = img.Width;
                        item.Height = img.Height;

                        // Update displayed image on UI thread
                        App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                        {
                            using var cutoutMs = new MemoryStream(cutoutBytes);
                            var cutoutRas = cutoutMs.AsRandomAccessStream();
                            var cutoutBmp = new BitmapImage();
                            await cutoutBmp.SetSourceAsync(cutoutRas);
                            item.Image = cutoutBmp;
                        });
                    }
                    catch (Exception ex)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            ErrorMessage = $"AI processing failed: {ex.Message}";
                        });
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
        for (int i = 0; i < HeadShots.Count; i++)
        {
            HeadShots[i].Index = i + 1;
        }
    }

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
            StatusMessage = $"Exported {ready.Count} head shots to {folderPath}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save head shots: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        HeadShots.Clear();
    }
}
