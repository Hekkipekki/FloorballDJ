using System.Globalization;
using System.Text;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;
using NAudio.Wave;

namespace FloorballDJ.Views;

public partial class ManageAudioFilesWindow : Window
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac", ".mp4", ".ogg" };
    private readonly MainViewModel _viewModel;
    private readonly ProjectService _projects;

    public ManageAudioFilesWindow(MainViewModel viewModel, ProjectService projects)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _projects = projects;
        RefreshStatistics();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatistics();

    private void RefreshStatistics()
    {
        var files = _viewModel.Decks
            .SelectMany(deck => deck.Jingles.Where(jingle => jingle.HasAudio)
                .Select(jingle => CreateStatus(deck.Name, jingle)))
            .ToList();
        var existingPaths = files.Where(file => !file.IsMissing).Select(file => file.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        long bytes = 0;
        foreach (var path in existingPaths)
            try { bytes += new FileInfo(path).Length; } catch { }

        FoundText.Text = files.Count(file => !file.IsMissing).ToString();
        MissingText.Text = files.Count(file => file.IsMissing).ToString();
        SizeText.Text = FormatBytes(bytes);
        FormatsText.Text = string.Join(", ", files.Select(file => Path.GetExtension(file.FilePath).TrimStart('.').ToUpperInvariant())
            .Where(format => format.Length > 0).Distinct().OrderBy(format => format)) is { Length: > 0 } formats ? formats : "–";
        FilesList.ItemsSource = files.OrderByDescending(file => file.IsMissing).ThenBy(file => file.DeckName).ThenBy(file => file.Title);
        EmptyLibraryText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(SearchFolderBox.Text))
            SearchFolderBox.Text = files.FirstOrDefault(file => file.IsMissing)?.SuggestedRoot ?? "";
    }

    private void BrowseSearchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Välj mappen med dina ljudfiler" };
        if (dialog.ShowDialog(this) == true) SearchFolderBox.Text = dialog.FolderName;
    }

    private async void SearchAndUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(SearchFolderBox.Text))
        {
            MessageBox.Show(this, "Välj först en giltig sökmapp.", "Sökmapp saknas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Söker igenom mappar…");
        try
        {
            var root = SearchFolderBox.Text;
            var discovered = await Task.Run(() => EnumerateAudioFiles(root));
            var byName = discovered.GroupBy(path => Path.GetFileName(path) ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var candidates = discovered.Select(path => new AudioCandidate(path, NormalizeFileName(path))).ToArray();
            var byNormalizedName = candidates.Where(candidate => candidate.NormalizedName.Length > 0)
                .GroupBy(candidate => candidate.NormalizedName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var updated = 0;
            foreach (var deck in _viewModel.Decks)
            {
                for (var index = 0; index < deck.Jingles.Count; index++)
                {
                    var jingle = deck.Jingles[index];
                    if (!jingle.HasAudio || (!UpdateAllCheck.IsChecked.GetValueOrDefault() && !jingle.IsMissing)) continue;
                    var match = FindBestMatch(jingle, byName, byNormalizedName, candidates);
                    if (match is null) continue;
                    if (string.Equals(jingle.FilePath, match, StringComparison.OrdinalIgnoreCase)) continue;
                    jingle.FilePath = match;
                    try
                    {
                        using var reader = new AudioFileReader(match);
                        jingle.DurationSeconds = reader.TotalTime.TotalSeconds;
                    }
                    catch { }
                    deck.Jingles[index] = jingle;
                    updated++;
                }
            }
            if (updated > 0) await _viewModel.SaveAsync();
            RefreshStatistics();
            StatusText.Text = updated == 0 ? "Inga nya matchningar hittades." : $"{updated} sökvägar uppdaterades och sparades.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Sökningen misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Sökningen kunde inte slutföras.";
        }
        finally { SetBusy(false); }
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
        try
        {
            var directory = await _projects.CreateMediaBackupAsync(_viewModel.Project, BackupFolderBox.Text);
            StatusText.Text = $"Backup skapad: {directory}";
            MessageBox.Show(this, $"Projekt och tillgängliga ljudfiler har kopierats till:\n{directory}",
                "Backup klar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backupen misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Backupen kunde inte skapas.";
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        SearchButton.IsEnabled = !busy;
        BackupButton.IsEnabled = !busy;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) StatusText.Text = status;
    }

    private static List<string> EnumerateAudioFiles(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (AudioExtensions.Contains(Path.GetExtension(file))) result.Add(file);
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result;
    }

    private static string? FindBestMatch(Jingle jingle, IReadOnlyDictionary<string, string> byName,
        IReadOnlyDictionary<string, AudioCandidate[]> byNormalizedName, IReadOnlyList<AudioCandidate> candidates)
    {
        var expectedFileName = Path.GetFileName(jingle.FilePath) ?? "";
        if (byName.TryGetValue(expectedFileName, out var exact)) return exact;

        var expectedKeys = new[] { NormalizeFileName(expectedFileName), NormalizeFileName(jingle.Title) }
            .Where(key => key.Length >= 6).Distinct(StringComparer.Ordinal).ToArray();
        if (expectedKeys.Length == 0) return null;
        foreach (var key in expectedKeys)
            if (byNormalizedName.TryGetValue(key, out var normalizedMatches) && normalizedMatches.Length == 1)
                return normalizedMatches[0].Path;

        var ranked = candidates
            .Select(candidate => new { candidate.Path, Score = expectedKeys.Max(key => FileNameSimilarity(key, candidate.NormalizedName)) })
            .Where(result => result.Score >= 0.78)
            .OrderByDescending(result => result.Score)
            .Take(2)
            .ToArray();
        if (ranked.Length == 0) return null;
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 0.08) return null;
        return ranked[0].Path;
    }

    private static string NormalizeFileName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        var ignored = new HashSet<string>(StringComparer.Ordinal) { "spotifydown" };
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => !ignored.Contains(token)));
    }

    private static double FileNameSimilarity(string expected, string candidate)
    {
        if (expected.Length == 0 || candidate.Length == 0) return 0;
        if (string.Equals(expected, candidate, StringComparison.Ordinal)) return 1;
        var shorter = expected.Length <= candidate.Length ? expected : candidate;
        var longer = expected.Length > candidate.Length ? expected : candidate;
        if (shorter.Length >= 8 && longer.Contains(shorter, StringComparison.Ordinal))
            return 0.88 + 0.12 * shorter.Length / longer.Length;

        var expectedTokens = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var shared = expectedTokens.Intersect(candidateTokens).Count();
        return expectedTokens.Count + candidateTokens.Count == 0
            ? 0
            : 2d * shared / (expectedTokens.Count + candidateTokens.Count);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static AudioFileStatus CreateStatus(string deckName, Jingle jingle)
    {
        if (!jingle.IsMissing)
            return new AudioFileStatus(deckName, jingle.Title, jingle.FilePath, false, "", "", "");

        var expectedFolder = Path.GetDirectoryName(jingle.FilePath) ?? "Okänd mapp";
        var folderExists = Directory.Exists(expectedFolder);
        var problem = folderExists
            ? $"Filen finns inte i den länkade mappen: {expectedFolder}"
            : $"Den länkade mappen finns inte längre: {expectedFolder}";
        var suggestedRoot = FindNearestExistingFolder(expectedFolder);
        var hint = $"Sök efter “{Path.GetFileName(jingle.FilePath)}” från: {(string.IsNullOrWhiteSpace(suggestedRoot) ? "välj musikbibliotekets rotmapp" : suggestedRoot)}";
        return new AudioFileStatus(deckName, jingle.Title, jingle.FilePath, true, problem, hint, suggestedRoot);
    }

    private static string FindNearestExistingFolder(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current)) return current;
            current = Path.GetDirectoryName(current) ?? "";
        }
        return "";
    }
}

internal sealed record AudioCandidate(string Path, string NormalizedName);

public sealed record AudioFileStatus(string DeckName, string Title, string FilePath, bool IsMissing, string Problem, string SearchHint, string SuggestedRoot);
