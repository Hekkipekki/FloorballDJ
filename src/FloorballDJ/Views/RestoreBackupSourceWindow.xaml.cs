using System.Windows;
using FloorballDJ.Services;
using Microsoft.Win32;

namespace FloorballDJ.Views;

public partial class RestoreBackupSourceWindow : Window
{
    public string? SourcePath { get; private set; }

    public RestoreBackupSourceWindow()
    {
        InitializeComponent();
        WindowPlacementService.FitToOwnerMonitor(this);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = LanguageService.Translate("Välj FloorballDJ-backupmappen") };
        if (picker.ShowDialog(this) != true) return;
        SourcePath = picker.FolderName;
        DialogResult = true;
    }

    private void ChoosePackage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = LanguageService.Translate("Välj flyttpaket"),
            Filter = "FloorballDJ-flyttpaket|*.fdjbackup"
        };
        if (picker.ShowDialog(this) != true) return;
        SourcePath = picker.FileName;
        DialogResult = true;
    }
}
