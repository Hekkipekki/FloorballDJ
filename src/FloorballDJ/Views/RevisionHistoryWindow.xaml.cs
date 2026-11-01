using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;

namespace FloorballDJ.Views;

public partial class RevisionHistoryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<ProjectRevision> _revisions = [];
    private ICollectionView? _historyView;

    public RevisionHistoryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _viewModel = viewModel;
        ProfileTitle.Text = viewModel.ProfileDisplayName;
        BackupPathText.Text = string.Format(LanguageService.Translate("Lokal backup: {0}"), Path.GetFileName(viewModel.LocalProfileBackupPath));
        BackupPathText.ToolTip = viewModel.LocalProfileBackupPath;
        RevisionList.ItemsSource = _revisions;
        _historyView = CollectionViewSource.GetDefaultView(_revisions);
        _historyView.Filter = MatchesFilter;
        Loaded += RevisionHistoryWindow_Loaded;
    }

    private async void RevisionHistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var revision in await _viewModel.GetRevisionsAsync()) _revisions.Add(revision);
            StatusText.Text = _revisions.Count == 0
                ? LanguageService.Translate("Det finns ännu inga återställningspunkter för profilen.")
                : string.Format(LanguageService.Translate("{0} återställningspunkter för den här profilen. Senaste visas överst."), _revisions.Count);
            if (_revisions.Count > 0) RevisionList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Historiken kunde inte läsas: {ex.Message}";
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreSelectedAsync();

    private void RevisionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreButton is null) return;
        var selected = RevisionList.SelectedItem is ProjectRevision;
        RestoreButton.IsEnabled = selected;
        RevisionDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SelectionHint.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool MatchesFilter(object item)
    {
        if (item is not ProjectRevision revision) return false;
        var kind = (HistoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        if (kind == "saved" && !revision.IsSavedVersion || kind == "draft" && revision.IsSavedVersion) return false;
        var query = HistorySearch.Text.Trim();
        return query.Length == 0 || $"{revision.ChangeDescription} {revision.ChangeLog} {revision.TimestampText} {revision.KindText}"
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void RefreshFilter()
    {
        _historyView?.Refresh();
        if (RevisionList is not null && RevisionList.Items.Count > 0 && RevisionList.SelectedItem is null) RevisionList.SelectedIndex = 0;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_viewModel.LocalProfileBackupPath)!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task RestoreSelectedAsync()
    {
        if (RevisionList.SelectedItem is not ProjectRevision revision) return;
        IsEnabled = false;
        try
        {
            await _viewModel.RestoreRevisionAsync(revision.Path);
            _viewModel.Status = string.Format(LanguageService.Translate("Återställde {0} för granskning. Spara manuellt för att ersätta originalprofilen."), revision.TimestampText);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Revisionen kunde inte återställas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }
}
