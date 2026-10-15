using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class TransferVersionWindow : Window
{
    public TransferVersion? SelectedVersion => VersionsList.SelectedItem as TransferVersion;
    public TransferVersionWindow(IReadOnlyList<TransferVersion> versions)
    {
        InitializeComponent();
        VersionsList.ItemsSource = versions;
        VersionsList.SelectedIndex = versions.Count > 0 ? 0 : -1;
        WindowPlacementService.FitToOwnerMonitor(this);
    }
    private void Import_Click(object sender, RoutedEventArgs e) { if (SelectedVersion is not null) DialogResult = true; }
}
