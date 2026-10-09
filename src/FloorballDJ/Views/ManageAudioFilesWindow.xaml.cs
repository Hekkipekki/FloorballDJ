using System.Windows;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;

namespace FloorballDJ.Views;

public partial class ManageAudioFilesWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ProjectService _projects;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _statisticsCancellation;
    private bool _closed;
    private bool _busy;
    internal AudioMetadataService Metadata { get; init; } = AudioMetadataService.Shared;
    private sealed record Target(Deck Deck, Jingle Jingle, AudioLibrarySource Source);

    public ManageAudioFilesWindow(MainViewModel viewModel, ProjectService projects)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _viewModel = viewModel;
        _projects = projects;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _statisticsCancellation?.Cancel();
            _statisticsCancellation?.Dispose();
            _lifetime.Dispose();
        };
        _ = RefreshStatisticsAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatisticsAsync();

    private Target[] CaptureTargets() => _viewModel.Decks.SelectMany(deck => deck.Jingles.Where(jingle => jingle.HasAudio)
        .Select(jingle => new Target(deck, jingle, new AudioLibrarySource(deck.Name, jingle.Title, jingle.FilePath)))).ToArray();

    internal async Task RefreshStatisticsAsync()
    {
        if (_closed) return;
        Dispatcher.VerifyAccess();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _viewModel.ProfileWorkCancellation);
        var token = cancellation.Token;
        var previous = _statisticsCancellation;
        _statisticsCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();
        var project = _viewModel.Project;
        var sources = CaptureTargets().Select(target => target.Source).ToArray();
        try
        {
            var statistics = await AudioLibraryService.InspectAsync(sources, token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(project, _viewModel.Project)) return;
            // An edit/relink during inspection invalidates this report, too.
            if (!sources.SequenceEqual(CaptureTargets().Select(target => target.Source))) return;
            var files = statistics.Files;
            FoundText.Text = files.Count(file => !file.IsMissing).ToString();
            MissingText.Text = files.Count(file => file.IsMissing).ToString();
            SizeText.Text = FormatBytes(statistics.Bytes);
            FormatsText.Text = statistics.Formats;
            FilesList.ItemsSource = files;
            EmptyLibraryText.Visibility = files.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(SearchFolderBox.Text))
                SearchFolderBox.Text = files.FirstOrDefault(file => file.IsMissing)?.SuggestedRoot ?? "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested) StatusText.Text = $"Sökningen kunde inte slutföras: {ex.Message}";
        }
    }

    private void BrowseSearchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Välj mappen med dina ljudfiler" };
        if (dialog.ShowDialog(this) == true) SearchFolderBox.Text = dialog.FolderName;
    }

    private async void SearchAndUpdate_Click(object sender, RoutedEventArgs e)
    {
        await SearchAndUpdateAsync(SearchFolderBox.Text, UpdateAllCheck.IsChecked.GetValueOrDefault());
    }

    internal async Task<int> SearchAndUpdateAsync(string root, bool updateAll)
    {
        if (_closed || _busy) return 0;
        Dispatcher.VerifyAccess();
        var project = _viewModel.Project;
        var targets = CaptureTargets();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _viewModel.ProfileWorkCancellation);
        var token = cancellation.Token;
        SetBusy(true, "Söker igenom mappar…");
        var updated = 0;
        try
        {
            if (!await Task.Run(() => Directory.Exists(root), token))
            {
                if (!_closed && !token.IsCancellationRequested)
                    MessageBox.Show(this, "Välj först en giltig sökmapp.", "Sökmapp saknas", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            var matches = await AudioLibraryService.FindMatchesAsync(root, targets.Select(target => target.Source).ToArray(), updateAll, Metadata, token);
            var performance = PerformanceDiagnostics.BeginOperation("LibraryRelinkApplyRequested");
            foreach (var batch in matches.Chunk(16))
            {
                if (_closed || token.IsCancellationRequested || !ReferenceEquals(project, _viewModel.Project)) break;
                var changed = false;
                using (performance.Measure("LibraryRelinkApplyBatch"))
                {
                    foreach (var match in batch)
                    {
                        var target = targets[match.SourceIndex];
                        if (!_viewModel.Decks.Contains(target.Deck) ||
                            target.Jingle.FilePath != target.Source.FilePath || target.Jingle.Title != target.Source.Title) continue;
                        var index = target.Deck.Jingles.IndexOf(target.Jingle);
                        if (index < 0) continue;
                        target.Jingle.FilePath = match.FilePath;
                        if (match.DurationSeconds is { } duration) target.Jingle.DurationSeconds = duration;
                        target.Deck.Jingles[index] = target.Jingle;
                        updated++;
                        changed = true;
                    }
                }
                // Persist partial progress, including when the window closes between batches.
                if (changed) _viewModel.RequestSave();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if (!_closed && !token.IsCancellationRequested && ReferenceEquals(project, _viewModel.Project))
            {
                if (updated > 0) await _viewModel.SaveAsync();
                await RefreshStatisticsAsync();
                if (!_closed && !token.IsCancellationRequested)
                    StatusText.Text = updated == 0 ? "Inga nya matchningar hittades." : $"{updated} sökvägar uppdaterades och sparades.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested)
            {
                MessageBox.Show(this, ex.Message, "Sökningen misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Sökningen kunde inte slutföras.";
            }
        }
        finally { if (!_closed) SetBusy(false); }
        return updated;
    }

    private void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Välj plats för backup" };
        if (dialog.ShowDialog(this) == true) BackupFolderBox.Text = dialog.FolderName;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(BackupFolderBox.Text))
        {
            MessageBox.Show(this, "Välj först en giltig backupmapp.", "Backupmapp saknas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Kopierar projekt och media…");
        PortableBackupResult? result = null;
        Exception? failure = null;
        var progressWindow = new BackupProgressWindow(
            "Skapar flyttbackup",
            "Skapar komplett flyttbackup",
            "Profil, inställningar och alla länkade ljudfiler kopieras till den valda platsen.") { Owner = this };
        try
        {
            progressWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);
            result = await _projects.CreateMediaBackupAsync(_viewModel.Project, BackupFolderBox.Text, progressWindow);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            progressWindow.CloseAfterOperation();
            SetBusy(false);
        }

        if (failure is not null)
        {
            MessageBox.Show(this, failure.Message, "Backupen misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Backupen kunde inte skapas.";
            return;
        }

        StatusText.Text = $"Backup skapad: {result!.Directory}";
        var warning = result.MissingFiles.Count == 0
            ? "Alla länkade ljudfiler följde med."
            : $"Varning: {result.MissingFiles.Count} ljudfiler saknades och kunde inte kopieras.";
        MessageBox.Show(this,
            $"Profil, inställningar och media har kopierats till:\n{result.Directory}\n\n" +
            $"{result.MediaFileCount} ljudfiler, {result.CustomFontCount} egna typsnitt och " +
            $"{result.RandomPoolProfileCount} profilbundna slumpgrupper och {result.TeamDeckProfileCount} Team Deck kopierades.\n{warning}",
            "Backup klar", MessageBoxButton.OK,
            result.MissingFiles.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        SearchButton.IsEnabled = !busy;
        BackupButton.IsEnabled = !busy;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) StatusText.Text = status;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

}
