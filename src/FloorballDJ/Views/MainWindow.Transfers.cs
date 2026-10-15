using System.Windows;
using FloorballDJ.Services;
using Microsoft.Win32;

namespace FloorballDJ.Views;

public partial class MainWindow
{
    private bool _transferInProgress;

    private async Task<bool> PrepareTransferAsync()
    {
        if (_transferInProgress) return false;
        if (ViewModel.HasUnsavedChanges || !ViewModel.HasNamedProfile) return await SaveProfileAsync();
        return true;
    }

    private string? PickTransferFolder(bool useRemembered)
    {
        var previous = _profilePreferences.GetTransferFolder();
        if (useRemembered && previous is not null && Directory.Exists(previous)) return previous;
        var picker = new OpenFolderDialog
        {
            Title = LanguageService.Translate("Välj föreningens mapp på Google Drive eller USB"),
            InitialDirectory = previous is not null && Directory.Exists(previous) ? previous : UserContentFolders.ProfilesDirectory
        };
        if (picker.ShowDialog(this) != true) return null;
        _profilePreferences.SetTransferFolder(picker.FolderName);
        return picker.FolderName;
    }

    private void ChooseTransferFolder_Click(object sender, RoutedEventArgs e) => PickTransferFolder(false);

    private async Task RunTransferAsync(string heading, Func<IProgress<PortableBackupProgress>, Task<string>> action)
    {
        if (_transferInProgress) return;
        _transferInProgress = true;
        var progress = new BackupProgressWindow(heading, heading,
            LanguageService.Translate("Profil, Autoplay-listor, aktuell kö och ljudfiler kontrolleras tillsammans.")) { Owner = this };
        IsEnabled = false;
        try
        {
            progress.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var result = await action(progress);
            progress.CloseAfterOperation();
            MessageBox.Show(this, result, LanguageService.Translate("Överföringen är klar"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            progress.CloseAfterOperation();
            MessageBox.Show(this, ex.Message, LanguageService.Translate("Överföringen kunde inte slutföras"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { progress.CloseAfterOperation(); IsEnabled = true; _transferInProgress = false; }
    }

    private async void TransferPackage_Click(object sender, RoutedEventArgs e)
    {
        if (!await PrepareTransferAsync()) return;
        var picker = new SaveFileDialog
        {
            Title = LanguageService.Translate("Spara flyttpaket"), Filter = "FloorballDJ-flyttpaket|*.fdjbackup",
            FileName = $"{ViewModel.ProfileDisplayName}-{DateTime.Now:yyyyMMdd-HHmm}.fdjbackup",
            InitialDirectory = _profilePreferences.GetTransferFolder() ?? UserContentFolders.ProfilesDirectory
        };
        if (picker.ShowDialog(this) != true) return;
        await RunTransferAsync(LanguageService.Translate("Skapar flyttpaket"), async progress =>
        {
            await _projects.CreateTransferPackageAsync(ViewModel.Project, picker.FileName, ViewModel.ProfileDisplayName, progress);
            return string.Format(LanguageService.Translate("Flyttpaketet är klart:\n{0}\n\nKopiera filen till USB eller Google Drive. På nästa dator väljer du Profil → Öppna flyttpaket. Låt Google Drive synka färdigt innan du byter dator."), picker.FileName);
        });
    }

    private async void OpenTransferPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_transferInProgress) return;
        var picker = new OpenFileDialog { Title = LanguageService.Translate("Öppna flyttpaket"), Filter = "FloorballDJ-flyttpaket|*.fdjbackup" };
        if (picker.ShowDialog(this) != true || !await ConfirmProfileChangesAsync()) return;
        await RunTransferAsync(LanguageService.Translate("Hämtar musikuppsättning"), async progress =>
        {
            var result = await _projects.RestoreTransferPackageAsync(picker.FileName, progress);
            await ActivateTransferAsync(result);
            return LocalTransferSummary(result);
        });
    }

    private async void SaveSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!await PrepareTransferAsync()) return;
        var folder = PickTransferFolder(true);
        if (folder is null) return;
        await RunTransferAsync(LanguageService.Translate("Sparar till synkmapp"), async progress =>
        {
            var version = await _projects.SaveToSyncFolderAsync(ViewModel.Project, folder, ViewModel.ProfileDisplayName, progress);
            return string.Format(LanguageService.Translate("Versionen är skapad:\n{0}\n\n{1} ljudfiler och {2} spellistor samt aktuell Autoplay-kö ingår. Oförändrade ljudfiler återanvänds.\n\nLåt Google Drive synka färdigt. På nästa dator väljer du Hämta från synkmapp. Tidigare versioner finns kvar."),
                version.DisplayName, version.Manifest.MediaFileCount, version.Manifest.PlaylistCount);
        });
    }

    private async void LoadSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_transferInProgress) return;
        var folder = PickTransferFolder(false);
        if (folder is null) return;
        var versions = _projects.GetTransferVersions(folder);
        if (versions.Count == 0)
        {
            MessageBox.Show(this, LanguageService.Translate("Inga färdiga versioner hittades. Kontrollera mappen och låt Google Drive synka färdigt."),
                LanguageService.Translate("Hämta musikuppsättning"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new TransferVersionWindow(versions) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedVersion is not { } version || !await ConfirmProfileChangesAsync()) return;
        await RunTransferAsync(LanguageService.Translate("Hämtar musikuppsättning"), async progress =>
        {
            var result = await _projects.RestoreTransferVersionAsync(folder, version, progress);
            await ActivateTransferAsync(result);
            return LocalTransferSummary(result);
        });
    }

    private async Task ActivateTransferAsync(PortableRestoreResult result)
    {
        _audio.StopAll();
        CloseTeamDeck();
        ViewModel.SetAutoplayMode(false);
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        await ViewModel.LoadAsync(result.ProfilePath);
        _profilePreferences.SetDefaultProfile(result.ProfilePath);
        RefreshRecentProfilesMenu();
        RefreshOutputName();
    }

    private static string LocalTransferSummary(PortableRestoreResult result)
        => result.MissingMediaCount == 0
            ? LanguageService.Translate("Musikuppsättningen är kontrollerad och sparad lokalt. Profil, spellistor och ljud är redo även utan Google Drive eller USB. Välj datorns ljudutgångar i Inställningar.")
            : string.Format(LanguageService.Translate("Överföringen är ofullständig: {0} filer saknas. Kontrollera ljudfiler och spellistor före användning."), result.MissingMediaCount);
}
