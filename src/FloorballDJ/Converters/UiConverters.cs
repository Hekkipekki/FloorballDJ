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
        try { return UiResourceCache.GetSolid(value?.ToString() ?? "#182338"); }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ButtonGradientConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return UiResourceCache.GetGradient(value?.ToString() ?? "#182338"); }
        catch { return UiResourceCache.GetGradient("invalid brush"); }
    }

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
    internal StableCollectionViews Views { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not IList source || values[1] is not int count)
            return Array.Empty<object>();
        var visibleCount = Math.Max(0, count);
        return Views.Get(source, null, 0, visibleCount, item =>
        {
            // Keep a live view of the actual deck collection. A detached ToList snapshot
            // redraws the old order after drag/drop even though the project was updated.
            var index = source.IndexOf(item);
            return index >= 0 && index < visibleCount;
        });
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class TakeSlotsConverter : IMultiValueConverter
{
    internal StableCollectionViews Views { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not IList source || values[1] is not Deck deck)
            return Array.Empty<object>();
        var page = values.Length > 2 && values[2] is int activePage ? Math.Max(0, activePage) : 0;
        var slots = deck.GetPageCapacity(page);
        var first = deck.GetPageStartIndex(page);
        var last = first + slots;
        return Views.Get(source, deck, first, last, item => item is Jingle jingle && jingle.Position >= first && jingle.Position < last);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class PageSelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is int pageNumber && values[1] is int activePage && pageNumber == activePage + 1;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class MultiplePagesVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
