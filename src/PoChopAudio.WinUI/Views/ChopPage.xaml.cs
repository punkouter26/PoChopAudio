using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Controls;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PoChopAudio.WinUI.Views;

public sealed partial class ChopPage : Page
{
    public ChopViewModel ViewModel { get; }

    public ChopPage()
    {
        ViewModel = App.GetService<ChopViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // LevelMeter is driven imperatively rather than by binding -- it repaints a bar, a dB
        // readout and a clip badge from one update. Nothing was calling UpdateLevel, so the meter
        // sat at its -inf dB default for the whole take; this is what connects it to the recorder.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        await ViewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChopViewModel.PeakDb):
            case nameof(ChopViewModel.RmsDb):
            case nameof(ChopViewModel.IsClipping):
                MicLevelMeter.UpdateLevel(ViewModel.PeakDb, ViewModel.RmsDb, ViewModel.IsClipping);
                break;

            case nameof(ChopViewModel.IsRecording) when !ViewModel.IsRecording:
                MicLevelMeter.Reset();
                break;
        }
    }

    public int ExportNormalizeIndex
    {
        get => (int)ViewModel.ExportKnobs.Normalize;
        set => ViewModel.ExportKnobs.Normalize = (NormalizeMode)value;
    }

    private async void OnBrowseFilesClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.PickFilesAsync(App.MainWindow);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to audio batch";
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

    private async void OnPlayOriginalClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChopFileItem item })
        {
            await ViewModel.PlayTakeAsync(item, null);
        }
    }

    private async void OnWaveformSegmentClicked(ChopFileItem item, ChopSegment? segment)
    {
        await ViewModel.PlayTakeAsync(item, segment);
    }

    private async void OnResplitSingleFileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChopFileItem item })
        {
            item.UsesOwnSettings = true;
            await ViewModel.ResplitOneAsync(item);
        }
    }

    private async void OnPlayTakeItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChopSegment segment })
        {
            // Find parent item
            var parentItem = ViewModel.Files.FirstOrDefault(f => f.Segments.Contains(segment));
            if (parentItem is not null)
            {
                await ViewModel.PlayTakeAsync(parentItem, segment);
            }
        }
    }

    private async void OnSaveTakeItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChopSegment segment })
        {
            var parentItem = ViewModel.Files.FirstOrDefault(f => f.Segments.Contains(segment));
            if (parentItem is not null)
            {
                await ViewModel.SaveTakeClipAsync((parentItem, segment, App.MainWindow));
            }
        }
    }
}

