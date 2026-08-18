using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class SettingsWindow : Window
{
    private readonly FloorballProject _target;
    private readonly ProfilePreferencesService _profilePreferences;
    private string? _randomPoolShortcut;
    public SettingsViewData ViewData { get; }

    public SettingsWindow(FloorballProject project, IReadOnlyList<OutputDevice> devices,
        ProfilePreferencesService profilePreferences)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _target = project;
        _profilePreferences = profilePreferences;
        var selectedDeckIds = project.Settings.RandomPoolDeckIds ?? [];
        var selectedJingleIds = project.Settings.RandomPoolJingleIds ?? [];
        ViewData = new SettingsViewData
        {
            Draft = Clone(project.Settings),
            Devices = devices,
            Fonts = new ObservableCollection<FontChoice>(FontService.GetFonts()),
            DefaultProfilePath = profilePreferences.GetDefaultProfilePath() ?? "",
            RandomPoolDecks = new ObservableCollection<RandomPoolDeckDraft>(project.Decks
                .Take(project.Settings.DeckCount)
                .Select(deck => new RandomPoolDeckDraft
                {
                    DeckId = deck.Id,
                    Name = deck.Name,
                    IncludeWholeDeck = selectedDeckIds.Contains(deck.Id),
                    Jingles = new ObservableCollection<RandomPoolJingleDraft>(deck.Jingles
                        .Where(jingle => jingle.HasAudio)
                        .Select(jingle => new RandomPoolJingleDraft
                        {
                            JingleId = jingle.Id,
                            Title = string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title,
                            IsIncluded = selectedJingleIds.Contains(jingle.Id)
                        }))
                }))
        };
        _randomPoolShortcut = ShortcutService.Normalize(project.Settings.RandomPoolShortcut);
        RandomPoolShortcutText.Text = _randomPoolShortcut ?? "<Ingen>";
        DataContext = ViewData;
    }

    private void ChooseRandomPoolShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutCaptureWindow(_randomPoolShortcut) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _randomPoolShortcut = ShortcutService.Normalize(dialog.SelectedShortcut);
        RandomPoolShortcutText.Text = _randomPoolShortcut ?? "<Ingen>";
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
        settings.RandomPoolShortcut = _randomPoolShortcut;
        settings.RandomPoolDeckIds = ViewData.RandomPoolDecks
            .Where(deck => deck.IncludeWholeDeck)
            .Select(deck => deck.DeckId)
            .Distinct()
            .ToList();
        settings.RandomPoolJingleIds = ViewData.RandomPoolDecks
            .SelectMany(deck => deck.Jingles)
            .Where(jingle => jingle.IsIncluded)
            .Select(jingle => jingle.JingleId)
            .Distinct()
            .ToList();
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
    }
}

public sealed class SettingsViewData
{
    public required AppSettings Draft { get; init; }
    public required IReadOnlyList<OutputDevice> Devices { get; init; }
    public required ObservableCollection<FontChoice> Fonts { get; init; }
    public required ObservableCollection<RandomPoolDeckDraft> RandomPoolDecks { get; init; }
    public string DefaultProfilePath { get; set; } = "";
    public string FontFolderPath => FontService.FontsDirectory;
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
