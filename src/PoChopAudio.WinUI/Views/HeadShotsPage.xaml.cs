using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI.Views;

public sealed partial class HeadShotsPage : Page
{
    public HeadShotsViewModel ViewModel { get; }
    private DispatcherTimer? _previewTimer;

    public HeadShotsPage()
    {
        ViewModel = App.GetService<HeadShotsViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private async void OnStartCameraClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartCameraAsync();
        if (ViewModel.IsCameraRunning)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _previewTimer.Tick += async (s, args) =>
            {
                if (ViewModel.IsCameraRunning && !ViewModel.IsCapturing)
                {
                    var frame = await ViewModel.Camera.CapturePreviewFrameAsync();
                    if (frame is not null)
                    {
                        CameraPreviewImage.Source = frame;
                    }
                }
            };
            _previewTimer.Start();
        }
    }

    private async void OnShootClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ShootAsync();
    }

    private void OnStopCameraClicked(object sender, RoutedEventArgs e)
    {
        _previewTimer?.Stop();
        ViewModel.StopCamera();
    }

    private void OnClearAllClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAll();
    }

    private async void OnSaveAllToFolderClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAllToFolderAsync(App.MainWindow);
    }

    private async void OnSaveHeadshotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HeadShotItem item })
        {
            await ViewModel.SaveHeadShotAsync((item, App.MainWindow));
        }
    }

    private void OnDeleteHeadshotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HeadShotItem item })
        {
            ViewModel.DeleteHeadShot(item);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _previewTimer?.Stop();
        _previewTimer = null;
        ViewModel.StopCamera();
    }
}

