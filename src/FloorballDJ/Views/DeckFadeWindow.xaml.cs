using System.Globalization;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class DeckFadeWindow : Window
{
    public double? FadeInSeconds { get; private set; }
    public double? FadeOutSeconds { get; private set; }

    public DeckFadeWindow(Deck deck)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        DeckNameText.Text = deck.Name;
        FadeInBox.Text = CommonValue(deck, item => item.FadeInOverrideSeconds);
        FadeOutBox.Text = CommonValue(deck, item => item.FadeOutOverrideSeconds);
        Loaded += (_, _) => { FadeInBox.Focus(); FadeInBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseSeconds(FadeInBox.Text, out var fadeIn) || !TryParseSeconds(FadeOutBox.Text, out var fadeOut))
        {
            MessageBox.Show(this, "Ange ett värde mellan 0 och 30 sekunder, eller lämna rutan tom för global inställning.",
                "Kontrollera fade-tiderna", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        FadeInSeconds = fadeIn;
        FadeOutSeconds = fadeOut;
        DialogResult = true;
    }

    private static bool TryParseSeconds(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 30)
            return false;
        value = parsed;
        return true;
    }

    private static string CommonValue(Deck deck, Func<Jingle, double?> selector)
    {
        var values = deck.Jingles.Where(item => item.HasAudio).Select(selector).Distinct().Take(2).ToArray();
        return values.Length == 1 && values[0] is double value
            ? value.ToString("0.###", CultureInfo.CurrentCulture)
            : "";
    }
}
