using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HarryPareto;

/// <summary>One coloured segment of a <see cref="SegmentBar"/> (task B — a per-camera slice).</summary>
public sealed class BarSegment
{
    /// <summary>Relative width of this segment (star weight); segments share the filled part of the bar.</summary>
    public double Weight { get; init; }
    public Brush Brush { get; init; } = Brushes.SteelBlue;
}

/// <summary>
/// A horizontal bar that fills a fraction of its track and splits that filled part into coloured
/// segments (task B — one segment per camera so a KF1/KF3 skew stays visible). Pure WPF, no charting
/// package: it is a <see cref="Grid"/> whose star-weighted columns are rebuilt in code whenever the
/// bound data changes. <see cref="Segments"/> give the filled columns; <see cref="RemainderWeight"/>
/// is the empty tail so every bar in the list shares one scale (weights are already relative to the
/// largest bar). With a single segment it renders exactly like a plain progress bar.
/// </summary>
public sealed class SegmentBar : Grid
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(SegmentBar),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RemainderWeightProperty = DependencyProperty.Register(
        nameof(RemainderWeight), typeof(double), typeof(SegmentBar),
        new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SegmentBar),
        new PropertyMetadata(Brushes.Transparent, OnChanged));

    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double RemainderWeight
    {
        get => (double)GetValue(RemainderWeightProperty);
        set => SetValue(RemainderWeightProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SegmentBar)d).Rebuild();

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        Children.Clear();
        Background = TrackBrush;

        const double eps = 0.0001; // a zero star-weight would collapse the column, so clamp to a hair
        var col = 0;
        if (Segments is not null)
        {
            foreach (var obj in Segments)
            {
                if (obj is not BarSegment seg)
                    continue;
                ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(seg.Weight, eps), GridUnitType.Star),
                });
                var fill = new Border { Background = seg.Brush };
                SetColumn(fill, col++);
                Children.Add(fill);
            }
        }

        // Empty tail so shorter bars do not stretch to full width (shared scale across the list).
        if (RemainderWeight > 0)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RemainderWeight, GridUnitType.Star) });
    }
}
