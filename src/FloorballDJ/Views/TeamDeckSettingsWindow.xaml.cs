using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class TeamDeckSettingsWindow : Window, INotifyPropertyChanged
{
    private readonly FloorballProject _project;
    private readonly Action<Jingle, double?, double?, double?> _previewPlay;
    private readonly Func<PlaybackSnapshot>? _playbackSnapshot;
    private TeamDeckDraft? _selectedTeam;

    public ObservableCollection<TeamDeckDraft> Teams { get; }
    public ObservableCollection<TeamJingleChoice> JingleChoices { get; }
    public TeamDeckDraft? SelectedTeam
    {
        get => _selectedTeam;
        set { if (_selectedTeam == value) return; _selectedTeam = value; Raise(); }
    }

    public TeamDeckSettingsWindow(FloorballProject project,
        Action<Jingle, double?, double?, double?>? previewPlay = null,
        Func<PlaybackSnapshot>? playbackSnapshot = null)
    {
        _project = project;
        _previewPlay = previewPlay ?? ((_, _, _, _) => { });
        _playbackSnapshot = playbackSnapshot;
        ProjectService.EnsureLayout(project);
        JingleChoices = new ObservableCollection<TeamJingleChoice>
        {
            new(null, "<Ingen vald>")
        };
        foreach (var deck in project.Decks)
            foreach (var jingle in deck.Jingles.Where(jingle => jingle.HasAudio))
                JingleChoices.Add(new TeamJingleChoice(jingle.Id,
                    $"{deck.Name}  ·  {(string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title)}"));

        Teams = new ObservableCollection<TeamDeckDraft>((project.Settings.TeamDeckProfiles ?? []).Select(Clone));
        var sourceJingles = project.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio)
            .GroupBy(jingle => jingle.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var player in Teams.SelectMany(team => team.Players).Where(player => player.StartSecondsOverride is null))
            if (player.JingleId is Guid jingleId && sourceJingles.TryGetValue(jingleId, out var sourceJingle))
                player.StartSecondsOverride = sourceJingle.StartSeconds;
        if (Teams.Count == 0) Teams.Add(new TeamDeckDraft { Name = "Hemmalag" });
        SelectedTeam = Teams[0];
        InitializeComponent();
        DataContext = this;
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
    }

    private static TeamDeckDraft Clone(TeamDeckProfile team) => new()
    {
        Id = team.Id == Guid.Empty ? Guid.NewGuid() : team.Id,
        Name = team.Name,
        Shortcut = ShortcutService.Normalize(team.Shortcut),
        DefaultJingleId = team.DefaultJingleId,
        TransitionAtSeconds = team.TransitionAtSeconds,
        DefaultFadeOutSeconds = team.DefaultFadeOutSeconds,
        PlayerFadeInSeconds = team.PlayerFadeInSeconds,
        Players = new ObservableCollection<TeamPlayerDraft>((team.Players ?? []).Select(player => new TeamPlayerDraft
        {
            Id = player.Id == Guid.Empty ? Guid.NewGuid() : player.Id,
            Number = player.Number,
            Name = player.Name,
            JingleId = player.JingleId,
            StartSecondsOverride = player.StartSecondsOverride,
            ButtonColor = player.ButtonColor,
            TextColor = player.TextColor
        }))
    };

    private void AddTeam_Click(object sender, RoutedEventArgs e)
    {
        var team = new TeamDeckDraft { Name = $"Lag {Teams.Count + 1}" };
        Teams.Add(team);
        SelectedTeam = team;
        TeamsList.ScrollIntoView(team);
    }

    private void RemoveTeam_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is null) return;
        if (MessageBox.Show(this, $"Ta bort Team Deck '{SelectedTeam.Name}'?", "Ta bort lag",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = Teams.IndexOf(SelectedTeam);
        Teams.Remove(SelectedTeam);
        SelectedTeam = Teams.Count == 0 ? null : Teams[Math.Clamp(index, 0, Teams.Count - 1)];
    }

    private void AddPlayer_Click(object sender, RoutedEventArgs e)
    {
        SelectedTeam?.Players.Add(new TeamPlayerDraft { Number = (SelectedTeam.Players.Count + 1).ToString(), Name = "Ny spelare" });
    }

    private void RemovePlayer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is null || (sender as FrameworkElement)?.DataContext is not TeamPlayerDraft player) return;
        SelectedTeam.Players.Remove(player);
    }

    private void ChooseDefaultJingle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is null) return;
        var picker = new TeamJinglePickerWindow(_project, SelectedTeam.DefaultJingleId) { Owner = this };
        if (picker.ShowDialog() == true) SelectedTeam.DefaultJingleId = picker.SelectedJingleId;
    }

    private void ChoosePlayerJingle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TeamPlayerDraft player) return;
        var picker = new TeamJinglePickerWindow(_project, player.JingleId) { Owner = this };
        if (picker.ShowDialog() != true) return;
        player.JingleId = picker.SelectedJingleId;
        player.StartSecondsOverride = picker.SelectedJingleId is null ? null : picker.SelectedStartSeconds;
    }

    private void PlayerJingle_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { IsDropDownOpen: true } combo ||
            combo.DataContext is not TeamPlayerDraft player) return;
        var choice = combo.SelectedItem as TeamJingleChoice;
        if (choice?.JingleId is not Guid id)
        {
            player.StartSecondsOverride = null;
            return;
        }
        var jingle = _project.Decks.SelectMany(deck => deck.Jingles).FirstOrDefault(item => item.Id == id);
        player.StartSecondsOverride = jingle?.StartSeconds;
    }

    private void PlayerStart_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox) CommitPlayerStart(textBox);
    }

    private void PlayerStart_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not System.Windows.Controls.TextBox textBox) return;
        CommitPlayerStart(textBox);
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static void CommitPlayerStart(System.Windows.Controls.TextBox textBox)
    {
        if (textBox.DataContext is not TeamPlayerDraft player) return;
        if (TryParseTime(textBox.Text, out var seconds)) player.StartSecondsOverride = Math.Max(0, seconds);
        textBox.Text = player.StartDisplay;
    }

    private static bool TryParseTime(string? value, out double seconds)
    {
        seconds = 0;
        var text = (value ?? "").Trim().Replace(',', '.');
        if (text.Length == 0) return true;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds)) return true;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!parts.All(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))) return false;
        var numbers = parts.Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        seconds = numbers.Length == 2 ? numbers[0] * 60 + numbers[1] : numbers[0] * 3600 + numbers[1] * 60 + numbers[2];
        return true;
    }

    private void TeamTiming_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox) CommitTeamTiming(textBox);
    }

    private void TeamTiming_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not System.Windows.Controls.TextBox textBox) return;
        CommitTeamTiming(textBox);
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static void CommitTeamTiming(System.Windows.Controls.TextBox textBox)
    {
        if (textBox.DataContext is not TeamDeckDraft team) return;
        var tag = textBox.Tag as string;
        if (tag == "Transition" && (string.IsNullOrWhiteSpace(textBox.Text) ||
            textBox.Text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase)))
            team.TransitionAtSeconds = null;
        else if (TryParseTime(textBox.Text, out var seconds))
        {
            seconds = Math.Clamp(seconds, 0, 30 * 60);
            if (tag == "Transition") team.TransitionAtSeconds = seconds;
            else if (tag == "FadeOut") team.DefaultFadeOutSeconds = Math.Clamp(seconds, 0, 30);
            else if (tag == "FadeIn") team.PlayerFadeInSeconds = Math.Clamp(seconds, 0, 30);
        }
        textBox.Text = tag switch
        {
            "Transition" => team.TransitionDisplay,
            "FadeOut" => team.DefaultFadeOutDisplay,
            _ => team.PlayerFadeInDisplay
        };
    }

    private void ChooseShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is null) return;
        var capture = new ShortcutCaptureWindow(SelectedTeam.Shortcut) { Owner = this };
        if (capture.ShowDialog() != true) return;
        SelectedTeam.Shortcut = capture.SelectedShortcut;
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is not null) SelectedTeam.Shortcut = null;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTeam is null) return;
        new TeamDeckWindow(ToModel(SelectedTeam), _project, _previewPlay, playbackSnapshot: _playbackSnapshot)
            { Owner = this }.ShowDialog();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var team in Teams)
        {
            team.Name = team.Name?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(team.Name))
            {
                MessageBox.Show(this, "Alla Team Deck måste ha ett namn.", "Kontrollera Team Deck", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var duplicate = Teams.Where(team => !string.IsNullOrWhiteSpace(team.Shortcut))
            .GroupBy(team => ShortcutKey(team.Shortcut)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"Snabbtangenten {duplicate.First().Shortcut} används av flera Team Deck.",
                "Snabbtangenten används redan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var keys = Teams.Select(team => ShortcutKey(team.Shortcut)).Where(key => key.Length > 0).ToHashSet();
        var conflicts = new List<string>();
        foreach (var profile in _project.Settings.RandomPoolProfiles ?? [])
            if (keys.Contains(ShortcutKey(profile.Shortcut))) conflicts.Add($"Slumpgrupp: {profile.Name}");
        if (keys.Contains(ShortcutKey(_project.Settings.AutoplayShortcut))) conflicts.Add("Autoplays standardspellista");
        foreach (var autoplay in _project.Settings.AutoplayProfiles ?? [])
            if (keys.Contains(ShortcutKey(autoplay.Shortcut))) conflicts.Add($"Autoplay-lista: {autoplay.Name}");
        foreach (var jingle in _project.Decks.SelectMany(deck => deck.Jingles))
        {
            if (keys.Contains(ShortcutKey(jingle.Shortcut))) conflicts.Add($"Jingle: {jingle.Title}");
            if (keys.Contains(ShortcutKey(jingle.CategoryShortcut))) conflicts.Add($"Slumpkategori: {jingle.Category}");
        }
        if (conflicts.Count > 0 && MessageBox.Show(this,
                "Snabbtangenten används redan av:\n\n" + string.Join("\n", conflicts.Distinct().Take(12)) +
                "\n\nVill du ersätta de befintliga kopplingarna?", "Ersätt snabbtangent",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var profile in _project.Settings.RandomPoolProfiles ?? [])
            if (keys.Contains(ShortcutKey(profile.Shortcut))) profile.Shortcut = null;
        if (keys.Contains(ShortcutKey(_project.Settings.RandomPoolShortcut))) _project.Settings.RandomPoolShortcut = null;
        if (keys.Contains(ShortcutKey(_project.Settings.AutoplayShortcut))) _project.Settings.AutoplayShortcut = null;
        foreach (var autoplay in _project.Settings.AutoplayProfiles ?? [])
            if (keys.Contains(ShortcutKey(autoplay.Shortcut))) autoplay.Shortcut = null;
        foreach (var jingle in _project.Decks.SelectMany(deck => deck.Jingles))
        {
            if (keys.Contains(ShortcutKey(jingle.Shortcut))) jingle.Shortcut = null;
            if (keys.Contains(ShortcutKey(jingle.CategoryShortcut))) jingle.CategoryShortcut = null;
        }

        _project.Settings.TeamDeckProfiles = Teams.Select(ToModel).ToList();
        DialogResult = true;
    }

    private static TeamDeckProfile ToModel(TeamDeckDraft team) => new()
    {
        Id = team.Id,
        Name = team.Name.Trim(),
        Shortcut = ShortcutService.Normalize(team.Shortcut),
        DefaultJingleId = team.DefaultJingleId,
        TransitionAtSeconds = team.TransitionAtSeconds,
        DefaultFadeOutSeconds = team.DefaultFadeOutSeconds,
        PlayerFadeInSeconds = team.PlayerFadeInSeconds,
        Players = team.Players.Select(player => new TeamDeckPlayer
        {
            Id = player.Id,
            Number = player.Number?.Trim() ?? "",
            Name = string.IsNullOrWhiteSpace(player.Name) ? "Spelare" : player.Name.Trim(),
            JingleId = player.JingleId,
            StartSecondsOverride = player.StartSecondsOverride,
            ButtonColor = player.ButtonColor,
            TextColor = player.TextColor
        }).ToList()
    };

    private static string ShortcutKey(string? shortcut) =>
        (ShortcutService.Normalize(shortcut) ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TeamDeckDraft : INotifyPropertyChanged
{
    private string _name = "Nytt lag";
    private string? _shortcut;
    private Guid? _defaultJingleId;
    private double? _transitionAtSeconds;
    private double _defaultFadeOutSeconds = 1.5;
    private double _playerFadeInSeconds = 0.75;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public string? Shortcut { get => _shortcut; set { if (_shortcut == value) return; _shortcut = value; Raise(); Raise(nameof(ShortcutDisplay)); } }
    public string ShortcutDisplay => ShortcutService.Normalize(Shortcut) ?? "<Ingen>";
    public Guid? DefaultJingleId { get => _defaultJingleId; set { if (_defaultJingleId == value) return; _defaultJingleId = value; Raise(); } }
    public double? TransitionAtSeconds
    {
        get => _transitionAtSeconds;
        set { if (_transitionAtSeconds == value) return; _transitionAtSeconds = value; Raise(); Raise(nameof(TransitionDisplay)); }
    }
    public double DefaultFadeOutSeconds
    {
        get => _defaultFadeOutSeconds;
        set { if (Math.Abs(_defaultFadeOutSeconds - value) < .001) return; _defaultFadeOutSeconds = value; Raise(); Raise(nameof(DefaultFadeOutDisplay)); }
    }
    public double PlayerFadeInSeconds
    {
        get => _playerFadeInSeconds;
        set { if (Math.Abs(_playerFadeInSeconds - value) < .001) return; _playerFadeInSeconds = value; Raise(); Raise(nameof(PlayerFadeInDisplay)); }
    }
    public string TransitionDisplay => TransitionAtSeconds is null ? "Auto" : FormatTime(TransitionAtSeconds.Value);
    public string DefaultFadeOutDisplay => DefaultFadeOutSeconds.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    public string PlayerFadeInDisplay => PlayerFadeInSeconds.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.fff") : time.ToString(@"m\:ss\.fff");
    }
    public ObservableCollection<TeamPlayerDraft> Players { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TeamPlayerDraft : INotifyPropertyChanged
{
    private string _number = "";
    private string _name = "Ny spelare";
    private Guid? _jingleId;
    private double? _startSecondsOverride;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get => _number; set { if (_number == value) return; _number = value; Raise(); } }
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public Guid? JingleId { get => _jingleId; set { if (_jingleId == value) return; _jingleId = value; Raise(); } }
    public double? StartSecondsOverride
    {
        get => _startSecondsOverride;
        set { if (_startSecondsOverride == value) return; _startSecondsOverride = value; Raise(); Raise(nameof(StartDisplay)); }
    }
    public string StartDisplay
    {
        get
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, StartSecondsOverride ?? 0));
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.fff") : time.ToString(@"m\:ss\.fff");
        }
    }
    public string ButtonColor { get; set; } = "#17304A";
    public string TextColor { get; set; } = "#F3F7FC";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TeamJingleChoice(Guid? JingleId, string Display);
