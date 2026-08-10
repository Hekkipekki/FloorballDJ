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
    public SettingsViewData ViewData { get; }

    public SettingsWindow(FloorballProject project, IReadOnlyList<OutputDevice> devices,
        ProfilePreferencesService profilePreferences)
    {
        InitializeComponent();
        _target = project;
        _profilePreferences = profilePreferences;
        ViewData = new SettingsViewData
        {
            Draft = Clone(project.Settings),
            Devices = devices,
            Fonts = new ObservableCollection<FontChoice>(FontService.GetFonts()),
            DefaultProfilePath = profilePreferences.GetDefaultProfilePath() ?? "",
            Decks = new ObservableCollection<DeckLayoutDraft>(project.Decks
                .Take(project.Settings.DeckCount)
                .Select(deck => new DeckLayoutDraft
                {
                    DeckId = deck.Id,
                    Name = deck.Name,
                    Rows = deck.Rows > 0 ? deck.Rows : project.Settings.Rows,
                    Columns = deck.Columns > 0 ? deck.Columns : project.Settings.Columns
                }))
        };
        DataContext = ViewData;
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
        if (settings.DeckCount is < 1 or > 20 ||
            ViewData.Decks.Any(deck => deck.Rows is < 1 or > ProjectService.MaximumDeckRows || deck.Columns is < 1 or > ProjectService.MaximumDeckColumns))
        {
            MessageBox.Show(this, $"Deck: 1–20. Rader: 1–{ProjectService.MaximumDeckRows}. Kolumner: 1–{ProjectService.MaximumDeckColumns}.",
                "Kontrollera layouten", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
        Copy(settings, _target.Settings);
        foreach (var draft in ViewData.Decks)
        {
            var deck = _target.Decks.FirstOrDefault(item => item.Id == draft.DeckId);
            if (deck is null) continue;
            deck.Rows = draft.Rows;
            deck.Columns = draft.Columns;
        }
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
    }
}

public sealed class SettingsViewData
{
    public required AppSettings Draft { get; init; }
    public required IReadOnlyList<OutputDevice> Devices { get; init; }
    public required ObservableCollection<FontChoice> Fonts { get; init; }
    public required ObservableCollection<DeckLayoutDraft> Decks { get; init; }
    public string DefaultProfilePath { get; set; } = "";
    public string FontFolderPath => FontService.FontsDirectory;
}

public sealed class DeckLayoutDraft
{
    public Guid DeckId { get; init; }
    public required string Name { get; init; }
    public int Rows { get; set; }
    public int Columns { get; set; }
}
