using System.Globalization;
using System.Windows.Data;
using FloorballDJ.Models;
using FloorballDJ.ViewModels;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public sealed class QueueRawFileConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not Jingle song || values[1] is not MainViewModel vm || !int.TryParse(values[2]?.ToString(), out var slot)) return parameter is string ? "" : false;
        var options = vm.GetQueueClipOptions(slot, song);
        if (parameter is not string) return options.UseRawFile;
        var label = LanguageService.Translate(options.UseRawFile ? "Hela låten" : "Deckversion");
        return options.EndSeconds is { } end ? $"{label} · {LanguageService.Translate("Slut")} {AutoplayClipWindow.Format(end)}" : label;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
