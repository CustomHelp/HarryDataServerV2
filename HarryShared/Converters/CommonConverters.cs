using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HarryShared.Converters;

/// <summary>bool Passed → green/red brush (OK/NG result cells).</summary>
public sealed class PassToBrushConverter : IValueConverter
{
    private static readonly Brush Pass = Make(0x22, 0xC5, 0x5E);
    private static readonly Brush Fail = Make(0xEF, 0x44, 0x44);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Pass : Fail;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>bool Passed → "PASS" / "FAIL" text.</summary>
public sealed class PassToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "PASS" : "FAIL";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps an integer/string result status (1=OK, 0=NG, -1=deleted) to a colour brush.
/// Accepts "OK"/"NG"/"DE" text or the numeric code.
/// </summary>
public sealed class ResultStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Make(0x22, 0xC5, 0x5E);
    private static readonly Brush Ng = Make(0xEF, 0x44, 0x44);
    private static readonly Brush Other = Make(0x6B, 0x72, 0x80);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim().ToUpperInvariant() ?? string.Empty;
        return text switch
        {
            "1" or "OK" or "GOOD" or "PASS" => Ok,
            "0" or "NG" or "BAD" or "FAIL" => Ng,
            _ => Other,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>null/empty → Collapsed, otherwise Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
