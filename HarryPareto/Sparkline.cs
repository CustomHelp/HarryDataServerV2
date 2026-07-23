using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HarryPareto;

/// <summary>
/// A tiny per-row sparkline: the window's N slice rates as small vertical bars (pure WPF, no charting
/// package). It is a <see cref="Grid"/> whose star-weighted columns + bottom-aligned bars are rebuilt
/// when <see cref="Values"/> changes. A value &lt; 0 marks an empty slice (no inspected parts → no bar).
/// Bars are scaled to the largest slice rate so the shape (rising / falling) is visible at a glance.
/// </summary>
public sealed class Sparkline : Grid
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Sparkline), new PropertyMetadata(Brushes.SteelBlue, OnChanged));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Sparkline)d).Rebuild();

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        Children.Clear();

        var vals = new List<double>();
        if (Values is not null)
            foreach (var o in Values)
                vals.Add(Convert.ToDouble(o));
        if (vals.Count == 0)
            return;

        var max = vals.Where(v => v >= 0).DefaultIfEmpty(0).Max();
        const double area = 16.0; // px of bar height; the control's Height should be ~ area + 2

        for (var i = 0; i < vals.Count; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var v = vals[i];
            if (v < 0)
                continue; // empty slice → leave the column blank
            var h = max > 0 ? Math.Max(1.5, v / max * area) : 1.5;
            var bar = new Border
            {
                Background = BarBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = h,
                Margin = new Thickness(0.5, 0, 0.5, 0),
                SnapsToDevicePixels = true,
            };
            SetColumn(bar, i);
            Children.Add(bar);
        }
    }
}
