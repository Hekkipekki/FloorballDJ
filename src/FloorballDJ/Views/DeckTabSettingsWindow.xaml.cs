using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class DeckTabSettingsWindow : Window
{
    public double TabWidth { get; private set; }
    public double TabHeight { get; private set; }
    public bool StartsNewRow => NewRowBox.IsChecked == true;

    public DeckTabSettingsWindow(Deck deck)
    {
        InitializeComponent();
        WindowPlacementService.FitToOwnerMonitor(this);
        DeckNameText.Text = deck.Name;
        WidthBox.Text = deck.TabWidth.ToString();
        HeightBox.Text = deck.TabHeight.ToString();
        NewRowBox.IsChecked = deck.TabStartsNewRow;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        WidthBox.Text = HeightBox.Text = "0";
        NewRowBox.IsChecked = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(WidthBox.Text, out var width) || !double.IsFinite(width) || (width != 0 && (width < 64 || width > 400)) ||
            !double.TryParse(HeightBox.Text, out var height) || !double.IsFinite(height) || (height != 0 && (height < 36 || height > 120)))
        {
            MessageBox.Show(this, "Ange bredd 64–400 och höjd 36–120, eller 0 för standard.", "Kontrollera måtten", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TabWidth = width;
        TabHeight = height;
        DialogResult = true;
    }
}
