using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.WinUI.Models;

namespace PoChopAudio.WinUI.Controls;

public sealed partial class BeforeAfterView : UserControl
{
    private CutoutFileItem? _item;

    public BeforeAfterView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(CutoutFileItem), typeof(BeforeAfterView),
            new PropertyMetadata(null, (d, e) => ((BeforeAfterView)d).OnItemChanged(e.NewValue as CutoutFileItem)));

    public CutoutFileItem? Item
    {
        get => (CutoutFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnItemChanged(CutoutFileItem? newItem)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = newItem;

        if (_item is not null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        UpdateImages();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CutoutFileItem.OriginalImage) or nameof(CutoutFileItem.CutoutImage))
        {
            DispatcherQueue.TryEnqueue(UpdateImages);
        }
    }

    private void UpdateImages()
    {
        OriginalImg.Source = _item?.OriginalImage;
        CutoutImg.Source = _item?.CutoutImage;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Resize response if needed
    }
}

