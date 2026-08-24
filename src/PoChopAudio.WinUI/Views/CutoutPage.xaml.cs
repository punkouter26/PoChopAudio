using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PoChopAudio.WinUI.Views;

public sealed partial class CutoutPage : Page
{
    public CutoutViewModel ViewModel { get; }

    public CutoutPage()
    {
        ViewModel = App.GetService<CutoutViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private async void OnBrowseFilesClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.PickFilesAsync(App.MainWindow);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to image batch";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                await ViewModel.AddFilesAsync(paths);
            }
        }
    }

    private async void OnSaveAllToFolderClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAllToFolderAsync(App.MainWindow);
    }

    private async void OnExportBatchZipClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ExportBatchZipAsync(App.MainWindow);
    }

    private async void OnSaveSingleCutoutClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CutoutFileItem item })
        {
            await ViewModel.SaveSingleCutoutAsync((item, App.MainWindow));
        }
    }

    private async void OnReprocessSingleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CutoutFileItem item })
        {
            await ViewModel.ReprocessOneAsync(item);
        }
    }
}

