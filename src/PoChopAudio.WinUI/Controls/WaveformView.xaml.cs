using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Models;

namespace PoChopAudio.WinUI.Controls;

public sealed partial class WaveformView : UserControl
{
    private ChopFileItem? _item;

    public event Action<ChopFileItem, ChopSegment?>? SegmentClicked;

    public WaveformView()
    {
        InitializeComponent();
        RootGrid.PointerPressed += OnPointerPressed;
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ChopFileItem), typeof(WaveformView),
            new PropertyMetadata(null, (d, e) => ((WaveformView)d).OnItemChanged(e.NewValue as ChopFileItem)));

    public ChopFileItem? Item
    {
        get => (ChopFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnItemChanged(ChopFileItem? newItem)
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

        Redraw();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChopFileItem.Waveform) or nameof(ChopFileItem.Segments))
        {
            DispatcherQueue.TryEnqueue(Redraw);
        }
        else if (e.PropertyName is nameof(ChopFileItem.PlayheadRatio) or nameof(ChopFileItem.IsPlaying))
        {
            DispatcherQueue.TryEnqueue(UpdatePlayhead);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    private void Redraw()
    {
        SegmentsCanvas.Children.Clear();
        WaveformCanvas.Children.Clear();

        if (_item is null)
        {
            StartTimeText.Text = "0:00.0";
            EndTimeText.Text = "0:00.0";
            PlayheadLine.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(RootBorder, "Waveform, empty");
            return;
        }

        var duration = _item.DurationSeconds;
        var width = ActualWidth > 0 ? ActualWidth : 600;
        var height = ActualHeight > 0 ? ActualHeight : 140;
        var midY = height / 2.0;

        StartTimeText.Text = "0:00.0";
        var ts = TimeSpan.FromSeconds(duration);
        EndTimeText.Text = ts.TotalMinutes >= 1 ? $"{ts.Minutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}" : $"{ts.Seconds}.{ts.Milliseconds / 100:D1}s";

        // The picture carries the whole result of a split, so the name has to carry it too. A
        // canvas of drawn rectangles is invisible to assistive tech no matter how it is decorated.
        AutomationProperties.SetName(
            RootBorder,
            $"Waveform of {_item.FileName}, {duration:F1} seconds, {_item.Segments.Count} sound{(_item.Segments.Count == 1 ? string.Empty : "s")} found");

        // 1. Draw segment highlight boxes
        if (duration > 0 && _item.Segments.Count > 0)
        {
            for (int i = 0; i < _item.Segments.Count; i++)
            {
                var seg = _item.Segments[i];
                var segStartX = (seg.StartSeconds / duration) * width;
                var segEndX = (seg.EndSeconds / duration) * width;
                var segWidth = Math.Max(2, segEndX - segStartX);

                // Alternating pleasant background tints for segments
                var isEven = i % 2 == 0;
                var bgBrush = isEven
                    ? new SolidColorBrush(ColorHelper.FromArgb(60, 59, 130, 246))   // blue tint
                    : new SolidColorBrush(ColorHelper.FromArgb(60, 16, 185, 129));  // emerald tint

                var borderBrush = isEven
                    ? new SolidColorBrush(ColorHelper.FromArgb(160, 59, 130, 246))
                    : new SolidColorBrush(ColorHelper.FromArgb(160, 16, 185, 129));

                var border = new Border
                {
                    Width = segWidth,
                    Height = height,
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1, 0, 1, 0),
                    CornerRadius = new CornerRadius(3)
                };
                Canvas.SetLeft(border, segStartX);
                Canvas.SetTop(border, 0);
                SegmentsCanvas.Children.Add(border);

                // Take badge
                var tagBorder = new Border
                {
                    Background = borderBrush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(2, 2, 0, 0),
                    Child = new TextBlock
                    {
                        Text = $"Take {seg.Index} ({seg.DurationSeconds:F1}s)",
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Colors.White)
                    }
                };
                Canvas.SetLeft(tagBorder, segStartX + 2);
                Canvas.SetTop(tagBorder, 4);
                SegmentsCanvas.Children.Add(tagBorder);
            }
        }

        // 2. Draw waveform bars
        var wave = _item.Waveform;
        if (wave.Count > 0)
        {
            var waveBrush = new SolidColorBrush(ColorHelper.FromArgb(200, 200, 220, 255));
            int numBars = Math.Min(wave.Count, (int)Math.Max(50, width / 3));
            double barWidth = width / numBars;
            double step = (double)wave.Count / numBars;

            for (int i = 0; i < numBars; i++)
            {
                int sampleIdx = Math.Min(wave.Count - 1, (int)(i * step));
                float amp = Math.Clamp(wave[sampleIdx], 0f, 1f);
                double barHeight = Math.Max(2, amp * (height - 30));

                var line = new Line
                {
                    X1 = i * barWidth + barWidth / 2,
                    Y1 = midY - barHeight / 2,
                    X2 = i * barWidth + barWidth / 2,
                    Y2 = midY + barHeight / 2,
                    Stroke = waveBrush,
                    StrokeThickness = Math.Max(1.5, barWidth - 1),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                WaveformCanvas.Children.Add(line);
            }
        }

        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        if (_item is null || !_item.IsPlaying)
        {
            PlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : 600;
        var x = _item.PlayheadRatio * width;
        PlayheadLine.X1 = x;
        PlayheadLine.X2 = x;
        PlayheadLine.Y2 = ActualHeight > 0 ? ActualHeight : 140;
        PlayheadLine.Visibility = Visibility.Visible;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_item is null) return;

        var pt = e.GetCurrentPoint(RootGrid).Position;
        var width = ActualWidth > 0 ? ActualWidth : 1;
        var clickRatio = Math.Clamp(pt.X / width, 0.0, 1.0);
        var clickSeconds = clickRatio * _item.DurationSeconds;

        // Check if clicked inside a segment
        var matched = _item.Segments.FirstOrDefault(s => clickSeconds >= s.StartSeconds && clickSeconds <= s.EndSeconds);
        SegmentClicked?.Invoke(_item, matched);
    }
}

