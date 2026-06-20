using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HarryDataServer.Converters;

/// <summary>bool Passed → green/red brush for MSA result cells.</summary>
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
