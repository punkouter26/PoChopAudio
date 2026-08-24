using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI.Views;

public sealed partial class HeadShotsPage : Page
{
    public HeadShotsViewModel ViewModel { get; }

    public HeadShotsPage()
    {
        ViewModel = App.GetService<HeadShotsViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public int CaptureModeIndex
    {
        get => ViewModel.IsBurstMode ? 0 : 1;
        set => ViewModel.IsBurstMode = (value == 0);
    }

    private async void OnStartCameraClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartCameraAsync();
        if (ViewModel.IsCameraRunning && ViewModel.Camera.Capture is not null)
        {
            CameraPreviewElement.Source = ViewModel.Camera.Capture;
            await ViewModel.Camera.Capture.StartPreviewAsync();
        }
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
        ViewModel.StopCamera();
    }
}

