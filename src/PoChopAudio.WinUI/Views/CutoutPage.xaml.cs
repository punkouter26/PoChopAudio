using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.ViewModels;
using Windows.Graphics.Imaging;

namespace PoChopAudio.WinUI.Views;

public sealed partial class CutoutPage : Page
{
    private readonly SoftwareBitmapSource _previewSource = new();

    /// <summary>
    /// Guards the viewfinder against frame pile-up. Frames arrive faster than
    /// <see cref="SoftwareBitmapSource.SetBitmapAsync"/> completes, so without this the queue grows
    /// without bound and the preview drifts seconds behind the room. Dropping frames is correct
    /// here — only the newest one is worth showing.
    /// </summary>
    private int _previewBusy;

    public CutoutViewModel ViewModel { get; }

    public CutoutPage()
    {
        ViewModel = App.GetService<CutoutViewModel>();
        InitializeComponent();

        PreviewImage.Source = _previewSource;
        ViewModel.Camera.FrameArrived += OnCameraFrame;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bring the viewfinder up straight away so TAKE PHOTO is the only thing to click.
        await ViewModel.StartCameraAsync();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Camera.FrameArrived -= OnCameraFrame;
        await ViewModel.Camera.StopAsync();
    }

    private void OnCameraFrame(SoftwareBitmap frame)
    {
        if (Interlocked.CompareExchange(ref _previewBusy, 1, 0) != 0)
        {
            return;
        }

        // The frame belongs to the camera thread and is disposed the moment this returns, so copy
        // before handing it to the UI thread.
        var copy = SoftwareBitmap.Copy(frame);

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await _previewSource.SetBitmapAsync(copy);
                }
                catch
                {
                    // The page went away between frames.
                }
                finally
                {
                    copy.Dispose();
                    Interlocked.Exchange(ref _previewBusy, 0);
                }
            }))
        {
            copy.Dispose();
            Interlocked.Exchange(ref _previewBusy, 0);
        }
    }




    private async void OnSaveAllToFolderClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAllToFolderAsync(App.MainWindow);
    }


    private async void OnSaveSingleCutoutClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CutoutFileItem item })
        {
            await ViewModel.SaveSingleCutoutAsync((item, App.MainWindow));
        }
    }

}
