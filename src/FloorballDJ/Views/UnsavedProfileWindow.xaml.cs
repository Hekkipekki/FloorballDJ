using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public enum UnsavedProfileChoice { Cancel, Save, Discard }

public partial class UnsavedProfileWindow : Window
{
    public UnsavedProfileChoice Choice { get; private set; }
    public UnsavedProfileWindow(string profileName, bool playlist = false)
    {
        InitializeComponent();
        if (playlist)
        {
            Title = LanguageService.Translate("Byt spellista");
            HeadingText.Text = LanguageService.Translate("Vill du spara spellistan först?");
            DescriptionText.Text = string.Format(LanguageService.Translate("Spellistan ‘{0}’ kommer att ersättas. Spara behåller listan i en spellistefil. Spara inte fortsätter utan att spara. Avbryt behåller den nuvarande listan."), profileName);
        }
        else
        DescriptionText.Text = string.Format(LanguageService.Translate("Profilen ‘{0}’ har osparade ändringar. Spara inte lämnar originalfilen orörd. En lokal säkerhetskopia och revisionshistoriken behålls."), profileName);
        WindowPlacementService.FitToOwnerMonitor(this);
    }
    private void Save_Click(object sender, RoutedEventArgs e) { Choice = UnsavedProfileChoice.Save; DialogResult = true; }
    private void Discard_Click(object sender, RoutedEventArgs e) { Choice = UnsavedProfileChoice.Discard; DialogResult = true; }
}
