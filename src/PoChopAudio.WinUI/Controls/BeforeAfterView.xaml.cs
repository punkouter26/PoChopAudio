using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PoChopAudio.WinUI.Models;
using Windows.Foundation;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// Before and after for one cut-out photo, on a checkerboard, with an optional edge outline and a
/// draggable wipe.
///
/// <para>
/// The previous version showed two static panes side by side on a flat dark panel. That hid the
/// only thing the fine-tune sliders actually change: a flat backdrop makes a translucent edge look
/// identical to an opaque one, so "feather 2" and "feather 0" were indistinguishable and the
/// sliders were being adjusted blind. A checkerboard shows alpha, and the edge outline shows
/// exactly where the mask boundary ended up.
/// </para>
/// </summary>
public sealed partial class BeforeAfterView : UserControl
{
    private const float CheckerSize = 10f;

    private static readonly Color CheckerLight = Color.FromArgb(255, 62, 68, 80);
    private static readonly Color CheckerDark = Color.FromArgb(255, 48, 54, 64);
    private static readonly Color DividerColor = Color.FromArgb(255, 226, 232, 240);
    private static readonly Color EdgeGlowColor = Color.FromArgb(255, 250, 204, 21);

    private CutoutFileItem? _item;
    private CanvasBitmap? _original;
    private CanvasBitmap? _cutout;

    /// <summary>Where the wipe divider sits, 0..1 across the control.</summary>
    private float _split = 0.5f;
    private bool _isDragging;

    public BeforeAfterView()
    {
        InitializeComponent();

        RootContainer.PointerPressed += OnPointerPressed;
        RootContainer.PointerMoved += OnPointerMoved;
        RootContainer.PointerReleased += OnPointerReleased;

        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(CutoutFileItem), typeof(BeforeAfterView),
            new PropertyMetadata(null, (d, e) => ((BeforeAfterView)d).OnItemChanged(e.NewValue as CutoutFileItem)));

    public CutoutFileItem? Item
    {
        get => (CutoutFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        DisposeBitmaps();
        Surface.RemoveFromVisualTree();
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

        _ = ReloadBitmapsAsync();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CutoutFileItem.Status) or nameof(CutoutFileItem.CutoutImage))
        {
            DispatcherQueue.TryEnqueue(() => _ = ReloadBitmapsAsync());
        }
    }

    private void OnRedrawRequested(object sender, RoutedEventArgs e) => Surface.Invalidate();

    /// <summary>
    /// Decodes both PNGs into Win2D bitmaps. Works from the byte arrays the item already holds
    /// rather than the <c>BitmapImage</c> thumbnails, because a XAML image source cannot be handed
    /// to a drawing session and re-encoding one to get at its pixels would be absurd.
    /// </summary>
    private async Task ReloadBitmapsAsync()
    {
        DisposeBitmaps();

        if (_item is null)
        {
            Surface.Invalidate();
            return;
        }

        try
        {
            var device = CanvasDevice.GetSharedDevice();

            if (_item.OriginalPngBytes is { Length: > 0 } originalBytes)
            {
                _original = await LoadAsync(device, originalBytes);
            }

            if (_item.CutoutPngBytes is { Length: > 0 } cutoutBytes)
            {
                _cutout = await LoadAsync(device, cutoutBytes);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A preview that will not decode is not worth an error banner; the card already shows
            // the item's own failure state if the cutout itself failed.
            DisposeBitmaps();
        }

        Surface.Invalidate();
    }

    private static async Task<CanvasBitmap> LoadAsync(CanvasDevice device, byte[] png)
    {
        using var stream = new MemoryStream(png);
        return await CanvasBitmap.LoadAsync(device, stream.AsRandomAccessStream());
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawCheckerboard(session, width, height);

        var split = SplitToggle.IsChecked == true;

        if (split)
        {
            DrawSplit(session, width, height);
        }
        else if (_cutout is not null)
        {
            session.DrawImage(_cutout, Fit(_cutout, width, height));
        }

        if (EdgeToggle.IsChecked == true && _cutout is not null)
        {
            DrawEdgeGlow(session, Fit(_cutout, width, height));
        }

        if (split)
        {
            var x = _split * width;
            session.DrawLine(x, 0, x, height, DividerColor, 2f);
            session.FillCircle(x, height / 2f, 9f, DividerColor);
            session.FillCircle(x, height / 2f, 6f, Color.FromArgb(255, 30, 41, 59));
        }
    }

    private void DrawCheckerboard(CanvasDrawingSession session, float width, float height)
    {
        for (var y = 0f; y < height; y += CheckerSize)
        {
            for (var x = 0f; x < width; x += CheckerSize)
            {
                var isLight = (int)((x / CheckerSize) + (y / CheckerSize)) % 2 == 0;
                session.FillRectangle(x, y, CheckerSize, CheckerSize, isLight ? CheckerLight : CheckerDark);
            }
        }
    }

    private void DrawSplit(CanvasDrawingSession session, float width, float height)
    {
        var divider = _split * width;

        // Left of the divider is the photo as taken, right of it is the cut-out. Clipping rather
        // than drawing two scaled halves keeps both sides in the same place on screen, which is
        // what makes the comparison meaningful.
        if (_original is not null)
        {
            using (session.CreateLayer(1f, new Rect(0, 0, divider, height)))
            {
                session.DrawImage(_original, Fit(_original, width, height));
            }
        }

        if (_cutout is not null)
        {
            using (session.CreateLayer(1f, new Rect(divider, 0, Math.Max(0, width - divider), height)))
            {
                session.DrawImage(_cutout, Fit(_cutout, width, height));
            }
        }
    }

    /// <summary>
    /// Outlines the mask boundary by running an edge-detection effect over the cut-out and tinting
    /// what survives. The alpha channel is where the boundary lives, so the source is the cut-out
    /// itself rather than anything derived.
    /// </summary>
    private void DrawEdgeGlow(CanvasDrawingSession session, Rect destination)
    {
        using var edges = new EdgeDetectionEffect
        {
            Source = _cutout,
            Amount = 0.6f,
            BlurAmount = 0.4f,
            Mode = EdgeDetectionEffectMode.Sobel,
        };

        using var tinted = new ColorMatrixEffect
        {
            Source = edges,
            ColorMatrix = new Matrix5x4
            {
                // Collapse whatever the detector produced onto one colour, keeping its strength as
                // alpha, so the outline reads as a single highlight rather than a rainbow fringe.
                M11 = 0, M12 = 0, M13 = 0, M14 = 0,
                M21 = 0, M22 = 0, M23 = 0, M24 = 1,
                M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                M41 = 0, M42 = 0, M43 = 0, M44 = 0,
                M51 = EdgeGlowColor.R / 255f,
                M52 = EdgeGlowColor.G / 255f,
                M53 = EdgeGlowColor.B / 255f,
                M54 = 0,
            },
        };

        // An effect has no intrinsic size the way a bitmap does, so the source rectangle has to say
        // which part of it to sample; the cut-out's own bounds are that region.
        session.DrawImage(tinted, destination, _cutout!.Bounds);
    }

    /// <summary>Letterboxes <paramref name="bitmap"/> into the control, preserving aspect ratio.</summary>
    private static Rect Fit(CanvasBitmap bitmap, float width, float height)
    {
        var source = bitmap.Size;

        if (source.Width <= 0 || source.Height <= 0)
        {
            return new Rect(0, 0, width, height);
        }

        var scale = Math.Min(width / source.Width, height / source.Height);
        var drawWidth = source.Width * scale;
        var drawHeight = source.Height * scale;

        return new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SplitToggle.IsChecked != true)
        {
            return;
        }

        _isDragging = true;
        RootContainer.CapturePointer(e.Pointer);
        UpdateSplit(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            UpdateSplit(e);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        RootContainer.ReleasePointerCapture(e.Pointer);
    }

    private void UpdateSplit(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(RootContainer).Position.X;
        _split = (float)Math.Clamp(x / Math.Max(1.0, RootContainer.ActualWidth), 0.0, 1.0);
        Surface.Invalidate();
    }

    private void DisposeBitmaps()
    {
        _original?.Dispose();
        _original = null;
        _cutout?.Dispose();
        _cutout = null;
    }
}
