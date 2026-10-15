using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public enum UnsavedProfileChoice { Cancel, Save, Discard }

public partial class UnsavedProfileWindow : Window
{
    public UnsavedProfileChoice Choice { get; private set; }
    public UnsavedProfileWindow(string profileName)
    {
        InitializeComponent();
        DescriptionText.Text = string.Format(LanguageService.Translate("Profilen ‘{0}’ har osparade ändringar. Spara inte tar även bort den tillfälliga arbetskopian."), profileName);
        WindowPlacementService.FitToOwnerMonitor(this);
    }
    private void Save_Click(object sender, RoutedEventArgs e) { Choice = UnsavedProfileChoice.Save; DialogResult = true; }
    private void Discard_Click(object sender, RoutedEventArgs e) { Choice = UnsavedProfileChoice.Discard; DialogResult = true; }
}
