using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class LanguageSelectionWindow : Window
{
    public string SelectedLanguage { get; private set; } = "en";

    public LanguageSelectionWindow()
    {
        InitializeComponent();
        WindowPlacementService.FitToOwnerMonitor(this);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedLanguage = SwedishChoice.IsChecked == true ? "sv" : "en";
        DialogResult = true;
    }
}
