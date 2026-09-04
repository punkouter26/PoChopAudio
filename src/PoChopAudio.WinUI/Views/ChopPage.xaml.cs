using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Controls;
using PoChopAudio.WinUI.Services;
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
        // InputScopeView is driven imperatively rather than by binding: it paints a scrolling trace,
        // a peak-hold marker and a numeric readout from two different feeds - level figures on the
        // view model, and raw decimated samples straight off the capture thread.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.BatchCompleted += OnBatchCompleted;
        App.GetService<AudioRecorderService>().ScopeSamplesAvailable += OnScopeSamples;
        await ViewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.BatchCompleted -= OnBatchCompleted;
        App.GetService<AudioRecorderService>().ScopeSamplesAvailable -= OnScopeSamples;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChopViewModel.PeakDb):
            case nameof(ChopViewModel.RmsDb):
            case nameof(ChopViewModel.IsClipping):
                MicScope.UpdateLevel(ViewModel.PeakDb, ViewModel.RmsDb, ViewModel.IsClipping);
                break;

            case nameof(ChopViewModel.IsRecording):
                // Confetti while the microphone is open would be CPU taken from the capture path,
                // which shows up as dropped frames in someone's take.
                Confetti.IsSuppressed = ViewModel.IsRecording;

                if (!ViewModel.IsRecording)
                {
                    MicScope.Reset();
                }

                break;
        }
    }

    /// <summary>Pushes captured audio into the live scope. Arrives on the capture thread.</summary>
    private void OnScopeSamples(float[] points) => MicScope.Push(points);

    private void OnBatchCompleted(bool succeeded)
    {
        if (succeeded)
        {
            Confetti.Burst();
        }
    }

    /// <summary>Gives each card its entrance and repositioning animations as it is realised.</summary>
    private void OnCardVisualLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: FrameworkElement card })
        {
            Motion.EnableListItemAnimations(card);
        }
    }

    private async void OnToggleSpectrogramClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChopFileItem item })
        {
            Motion.Pulse((Button)sender);
            await ViewModel.ToggleSpectrogramAsync(item);
        }
    }

    private void OnWaveformScrubbed(ChopFileItem item, double seconds)
    {
        ViewModel.Seek(item, seconds);
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

