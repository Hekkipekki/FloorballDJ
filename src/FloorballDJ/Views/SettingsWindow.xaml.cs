using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class SettingsWindow : Window
{
    private readonly FloorballProject _target;
    private readonly ProfilePreferencesService _profilePreferences;
    private readonly HashSet<string> _confirmedShortcutReplacements = new(StringComparer.OrdinalIgnoreCase);
    public SettingsViewData ViewData { get; }

    public SettingsWindow(FloorballProject project, IReadOnlyList<OutputDevice> devices,
        ProfilePreferencesService profilePreferences)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _target = project;
        _profilePreferences = profilePreferences;
        var profileDrafts = new ObservableCollection<RandomPoolProfileDraft>(
            (project.Settings.RandomPoolProfiles ?? []).Select(profile => CreateRandomProfileDraft(profile, project.Decks)));
        if (profileDrafts.Count == 0)
            profileDrafts.Add(CreateRandomProfileDraft(new RandomPoolProfile { Name = "Slumpgrupp 1" }, project.Decks));
        ViewData = new SettingsViewData
        {
            Draft = Clone(project.Settings),
            Devices = devices,
            Fonts = new ObservableCollection<FontChoice>(FontService.GetFonts()),
            DefaultProfilePath = profilePreferences.GetDefaultProfilePath() ?? "",
            RecentProfiles = new ObservableCollection<RecentProfileChoice>(profilePreferences.GetRecentProfiles()
                .Select(path => new RecentProfileChoice(Path.GetFileNameWithoutExtension(path), path))),
            RandomPoolProfiles = profileDrafts
        };
        ViewData.SelectedRandomPoolProfile = profileDrafts[0];
        DataContext = ViewData;
    }

    private void ChooseRandomPoolShortcut_Click(object sender, RoutedEventArgs e)
    {
        var profile = ViewData.SelectedRandomPoolProfile;
        if (profile is null) return;
        var dialog = new ShortcutCaptureWindow(profile.Shortcut) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var shortcut = ShortcutService.Normalize(dialog.SelectedShortcut);
        if (!ConfirmAndStageShortcutReplacement(profile, shortcut)) return;
        profile.Shortcut = shortcut;
    }

    private void AddRandomPoolProfile_Click(object sender, RoutedEventArgs e)
    {
        var number = 1;
        string name;
        do name = $"Slumpgrupp {number++}";
        while (ViewData.RandomPoolProfiles.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        var profile = CreateRandomProfileDraft(new RandomPoolProfile { Name = name }, _target.Decks);
        ViewData.RandomPoolProfiles.Add(profile);
        ViewData.SelectedRandomPoolProfile = profile;
    }

    private void RemoveRandomPoolProfile_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewData.SelectedRandomPoolProfile;
        if (selected is null) return;
        var index = ViewData.RandomPoolProfiles.IndexOf(selected);
        ViewData.RandomPoolProfiles.Remove(selected);
        if (ViewData.RandomPoolProfiles.Count == 0)
            ViewData.RandomPoolProfiles.Add(CreateRandomProfileDraft(new RandomPoolProfile { Name = "Slumpgrupp 1" }, _target.Decks));
        ViewData.SelectedRandomPoolProfile = ViewData.RandomPoolProfiles[Math.Clamp(index, 0, ViewData.RandomPoolProfiles.Count - 1)];
    }

    private void OpenFontFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FontService.FontsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontService.FontsDirectory}\"") { UseShellExecute = true });
    }

    private void ReloadFonts_Click(object sender, RoutedEventArgs e)
    {
        ViewData.Fonts.Clear();
        foreach (var font in FontService.GetFonts()) ViewData.Fonts.Add(font);
        FontStatusText.Text = $"{ViewData.Fonts.Count} typsnitt hittades";
    }

    private void ChooseDefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "FloorballDJ-projekt|*.floorballdj.json|Alla filer|*.*",
            CheckFileExists = true,
            Title = "Välj standardprofil"
        };
        if (!string.IsNullOrWhiteSpace(ViewData.DefaultProfilePath))
        {
            try { dialog.InitialDirectory = Path.GetDirectoryName(ViewData.DefaultProfilePath); }
            catch { }
        }
        if (dialog.ShowDialog(this) != true) return;
        ViewData.DefaultProfilePath = dialog.FileName;
        DefaultProfilePathText.Text = dialog.FileName;
    }

    private void ClearDefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        ViewData.DefaultProfilePath = "";
        DefaultProfilePathText.Text = "";
    }

    private void RecentProfilesCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentProfilesCombo.SelectedValue is not string path) return;
        ViewData.DefaultProfilePath = path;
        DefaultProfilePathText.Text = path;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = ViewData.Draft;
        settings.TitleFontSize = Math.Clamp(settings.TitleFontSize, 9, 40);
        settings.DefaultLoudnessTargetLufs = Math.Clamp(settings.DefaultLoudnessTargetLufs, -30, 0);
        settings.MasterLimiterCeilingDbtp = Math.Clamp(settings.MasterLimiterCeilingDbtp, -12, 0);
        settings.TalkDuckLevelDb = Math.Clamp(settings.TalkDuckLevelDb, -60, 0);
        try { _profilePreferences.SetDefaultProfile(ViewData.DefaultProfilePath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kunde inte spara standardprofil",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var duplicateShortcut = ViewData.RandomPoolProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Shortcut))
            .GroupBy(profile => ShortcutService.Normalize(profile.Shortcut), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateShortcut is not null)
        {
            MessageBox.Show(this, $"Snabbtangenten {duplicateShortcut.Key} används av flera slumpgrupper. Välj en unik tangent för varje grupp.",
                "Dubblett av snabbtangent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        settings.RandomPoolProfiles = ViewData.RandomPoolProfiles.Select(profile => new RandomPoolProfile
        {
            Id = profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim(),
            Shortcut = ShortcutService.Normalize(profile.Shortcut),
            DeckIds = profile.Decks.Where(deck => deck.IncludeWholeDeck).Select(deck => deck.DeckId).Distinct().ToList(),
            JingleIds = profile.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.IsIncluded)
                .Select(jingle => jingle.JingleId).Distinct().ToList()
        }).ToList();
        // De äldre fälten behålls tomma; gamla profiler migreras till listan när de öppnas.
        settings.RandomPoolShortcut = null;
        settings.RandomPoolDeckIds = [];
        settings.RandomPoolJingleIds = [];
        var activeReplacements = settings.RandomPoolProfiles.Select(profile => ShortcutService.Normalize(profile.Shortcut))
            .Where(shortcut => shortcut is not null && _confirmedShortcutReplacements.Contains(shortcut))
            .Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var jingle in _target.Decks.SelectMany(deck => deck.Jingles))
        {
            if (activeReplacements.Contains(ShortcutService.Normalize(jingle.Shortcut) ?? "")) jingle.Shortcut = null;
            if (activeReplacements.Contains(ShortcutService.Normalize(jingle.CategoryShortcut) ?? "")) jingle.CategoryShortcut = null;
        }
        Copy(settings, _target.Settings);
        DialogResult = true;
    }

    private static AppSettings Clone(AppSettings source)
    {
        var result = new AppSettings();
        Copy(source, result);
        result.OutputDeviceId ??= "";
        return result;
    }

    private static void Copy(AppSettings source, AppSettings target)
    {
        target.DeckCount = source.DeckCount;
        target.Rows = source.Rows;
        target.Columns = source.Columns;
        target.ButtonHeight = source.ButtonHeight;
        target.ButtonWidth = source.ButtonWidth;
        target.TitleFontSize = source.TitleFontSize;
        target.FontFamily = source.FontFamily;
        target.OutputDeviceId = source.OutputDeviceId;
        target.SecondaryOutputDeviceId = source.SecondaryOutputDeviceId;
        target.MusicFolderPath = source.MusicFolderPath;
        target.MasterVolumeDb = source.MasterVolumeDb;
        target.FadeInSeconds = source.FadeInSeconds;
        target.FadeOutSeconds = source.FadeOutSeconds;
        target.AutoplayTransitionSeconds = source.AutoplayTransitionSeconds;
        target.DuckLevelDb = source.DuckLevelDb;
        target.TalkDuckLevelDb = source.TalkDuckLevelDb;
        target.TrackSession = source.TrackSession;
        target.DefaultLoudnessTargetLufs = source.DefaultLoudnessTargetLufs;
        target.MasterLimiterEnabled = source.MasterLimiterEnabled;
        target.MasterLimiterCeilingDbtp = source.MasterLimiterCeilingDbtp;
        target.AutoMixHeadroomEnabled = source.AutoMixHeadroomEnabled;
        target.RandomPoolShortcut = ShortcutService.Normalize(source.RandomPoolShortcut);
        target.RandomPoolDeckIds = source.RandomPoolDeckIds?.Distinct().ToList() ?? [];
        target.RandomPoolJingleIds = source.RandomPoolJingleIds?.Distinct().ToList() ?? [];
        target.RandomPoolProfiles = source.RandomPoolProfiles?.Select(profile => new RandomPoolProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Shortcut = ShortcutService.Normalize(profile.Shortcut),
            DeckIds = profile.DeckIds?.Distinct().ToList() ?? [],
            JingleIds = profile.JingleIds?.Distinct().ToList() ?? []
        }).ToList() ?? [];
    }

    private static RandomPoolProfileDraft CreateRandomProfileDraft(RandomPoolProfile profile, IEnumerable<Deck> decks)
    {
        var selectedDeckIds = (profile.DeckIds ?? []).ToHashSet();
        var selectedJingleIds = (profile.JingleIds ?? []).ToHashSet();
        return new RandomPoolProfileDraft
        {
            Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name,
            Shortcut = ShortcutService.Normalize(profile.Shortcut),
            Decks = new ObservableCollection<RandomPoolDeckDraft>(decks
                .Where(deck => deck.Jingles.Any(jingle => jingle.HasAudio))
                .Select(deck => new RandomPoolDeckDraft
            {
                DeckId = deck.Id,
                Name = deck.Name,
                IncludeWholeDeck = selectedDeckIds.Contains(deck.Id),
                Jingles = new ObservableCollection<RandomPoolJingleDraft>(deck.Jingles.Where(jingle => jingle.HasAudio).Select(jingle => new RandomPoolJingleDraft
                {
                    JingleId = jingle.Id,
                    Title = string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title,
                    IsIncluded = selectedJingleIds.Contains(jingle.Id)
                }))
            }))
        };
    }

    private bool ConfirmAndStageShortcutReplacement(RandomPoolProfileDraft selected, string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return true;
        var conflictingProfiles = ViewData.RandomPoolProfiles
            .Where(profile => profile != selected && string.Equals(ShortcutService.Normalize(profile.Shortcut), shortcut, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var conflictingJingles = _target.Decks.SelectMany(deck => deck.Jingles)
            .Where(jingle => string.Equals(ShortcutService.Normalize(jingle.Shortcut), shortcut, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ShortcutService.Normalize(jingle.CategoryShortcut), shortcut, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (conflictingProfiles.Length == 0 && conflictingJingles.Length == 0) return true;

        var owners = conflictingProfiles.Select(profile => $"slumpgruppen ‘{profile.Name}’")
            .Concat(conflictingJingles.Select(jingle =>
                string.Equals(ShortcutService.Normalize(jingle.Shortcut), shortcut, StringComparison.OrdinalIgnoreCase)
                    ? $"jinglen ‘{jingle.Title}’"
                    : $"slumpkategorin för ‘{jingle.Title}’"))
            .Distinct()
            .Take(6);
        var message = $"Snabbtangenten {shortcut} används redan av {string.Join(", ", owners)}.\n\nVill du ersätta den gamla kopplingen?";
        if (MessageBox.Show(this, message, "Snabbtangenten används redan", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;

        foreach (var profile in conflictingProfiles) profile.Shortcut = null;
        _confirmedShortcutReplacements.Add(shortcut);
        return true;
    }
}

public sealed class SettingsViewData : INotifyPropertyChanged
{
    private RandomPoolProfileDraft? _selectedRandomPoolProfile;
    public required AppSettings Draft { get; init; }
    public required IReadOnlyList<OutputDevice> Devices { get; init; }
    public required ObservableCollection<FontChoice> Fonts { get; init; }
    public required ObservableCollection<RandomPoolProfileDraft> RandomPoolProfiles { get; init; }
    public required ObservableCollection<RecentProfileChoice> RecentProfiles { get; init; }
    public RandomPoolProfileDraft? SelectedRandomPoolProfile
    {
        get => _selectedRandomPoolProfile;
        set { if (ReferenceEquals(_selectedRandomPoolProfile, value)) return; _selectedRandomPoolProfile = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRandomPoolProfile))); }
    }
    public string DefaultProfilePath { get; set; } = "";
    public string FontFolderPath => FontService.FontsDirectory;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record RecentProfileChoice(string Name, string Path);

public sealed class RandomPoolProfileDraft : INotifyPropertyChanged
{
    private string _name = "Slumpgrupp";
    private string? _shortcut;
    public Guid Id { get; init; }
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public string? Shortcut { get => _shortcut; set { if (_shortcut == value) return; _shortcut = value; Raise(); Raise(nameof(ShortcutDisplay)); } }
    public string ShortcutDisplay => Shortcut ?? "<Ingen>";
    public required ObservableCollection<RandomPoolDeckDraft> Decks { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RandomPoolDeckDraft
{
    public Guid DeckId { get; init; }
    public required string Name { get; init; }
    public bool IncludeWholeDeck { get; set; }
    public required ObservableCollection<RandomPoolJingleDraft> Jingles { get; init; }
}

public sealed class RandomPoolJingleDraft
{
    public Guid JingleId { get; init; }
    public required string Title { get; init; }
    public bool IsIncluded { get; set; }
}
