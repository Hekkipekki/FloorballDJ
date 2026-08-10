using System.Globalization;
using System.Collections;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FloorballDJ.Models;
using FloorballDJ.ViewModels;

namespace FloorballDJ.Converters;

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return new BrushConverter().ConvertFromString(value?.ToString() ?? "#182338") ?? Brushes.Transparent; }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ButtonGradientConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var parsed = new BrushConverter().ConvertFromString(value?.ToString() ?? "#182338");
            var color = parsed is SolidColorBrush solid ? solid.Color : Color.FromRgb(24, 35, 56);
            var light = Mix(color, Colors.White, 0.16);
            var dark = Mix(color, Colors.Black, 0.20);
            return new LinearGradientBrush(
            [
                new GradientStop(light, 0),
                new GradientStop(color, 0.46),
                new GradientStop(dark, 0.72),
                new GradientStop(Mix(color, Colors.White, 0.07), 1)
            ], new Point(0, 0), new Point(1, 1));
        }
        catch { return new SolidColorBrush(Color.FromRgb(24, 35, 56)); }
    }

    private static Color Mix(Color source, Color target, double amount) => Color.FromArgb(source.A,
        (byte)(source.R + (target.R - source.R) * amount),
        (byte)(source.G + (target.G - source.G) * amount),
        (byte)(source.B + (target.B - source.B) * amount));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class FlexibleDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? number.ToString("0.###", culture) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : Binding.DoNothing;
    }
}

public sealed class FontFamilyValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Services.FontService.Resolve(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ActiveJingleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is Guid jingleId && values[1] is Guid activeId && jingleId == activeId;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds) is var time && time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss")
            : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DbMeterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is float db ? Math.Clamp(db + 60, 0, 60) : 0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TakeCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not System.Collections.IEnumerable source || values[1] is not int count)
            return Array.Empty<object>();
        return source.Cast<object>().Take(Math.Max(0, count)).ToList();
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class TakeSlotsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not IList source || values[1] is not int rows || values[2] is not int columns)
            return Array.Empty<object>();
        var slots = Math.Max(0, rows * columns);
        return new ListCollectionView(source)
        {
            Filter = item => item is Jingle jingle && jingle.Position < slots
        };
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
