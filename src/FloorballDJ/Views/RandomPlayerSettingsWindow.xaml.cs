using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class RandomPlayerSettingsWindow : Window
{
    private readonly FloorballProject _project;
    private readonly HashSet<string> _replacementShortcuts = new(StringComparer.OrdinalIgnoreCase);
    public RandomPlayerSettingsViewData ViewData { get; }

    public RandomPlayerSettingsWindow(FloorballProject project)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _project = project;
        ProjectService.EnsureLayout(project);
        var setups = new ObservableCollection<RandomPlayerSetupEditor>(
            project.Settings.RandomPoolSetups.Select(CreateSetupEditor));
        if (setups.Count == 0)
            setups.Add(CreateSetupEditor(new RandomPoolSetup { Name = "Standard" }));
        ViewData = new RandomPlayerSettingsViewData { Setups = setups };
        ViewData.SelectedSetup = setups.FirstOrDefault(setup => setup.Id == project.Settings.ActiveRandomPoolSetupId)
                                 ?? setups[0];
        DataContext = ViewData;
        Loaded += (_, _) => RefreshOverview();
    }

    private RandomPlayerSetupEditor CreateSetupEditor(RandomPoolSetup setup)
    {
        var profiles = new ObservableCollection<RandomPlayerProfileEditor>(
            (setup.Profiles ?? []).Select(CreateEditor));
        if (profiles.Count == 0) profiles.Add(CreateEditor(new RandomPoolProfile { Name = "Slumpgrupp 1" }));
        return new RandomPlayerSetupEditor
        {
            Id = setup.Id == Guid.Empty ? Guid.NewGuid() : setup.Id,
            Name = string.IsNullOrWhiteSpace(setup.Name) ? "Slumpprofil" : setup.Name.Trim(),
            Profiles = profiles
        };
    }

    private RandomPlayerProfileEditor CreateEditor(RandomPoolProfile profile)
    {
        var deckIds = (profile.DeckIds ?? []).ToHashSet();
        var jingleIds = (profile.JingleIds ?? []).ToHashSet();
        var editor = new RandomPlayerProfileEditor
        {
            Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim(),
            Shortcut = ShortcutService.Normalize(profile.Shortcut)
        };
        foreach (var deck in _project.Decks.Where(deck => deck.Jingles.Any(jingle => jingle.HasAudio && File.Exists(jingle.FilePath))))
        {
            var deckEditor = new RandomPlayerDeckEditor
            {
                DeckId = deck.Id,
                Name = deck.Name,
                IncludeWholeDeck = deckIds.Contains(deck.Id)
            };
            var originalOrder = 0;
            foreach (var jingle in deck.Jingles.Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath)))
            {
                var item = new RandomPlayerJingleEditor
                {
                    JingleId = jingle.Id,
                    Title = string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title,
                    SearchText = $"{jingle.Title} {Path.GetFileNameWithoutExtension(jingle.FilePath)}",
                    DurationSeconds = Math.Max(0, jingle.EndSeconds.GetValueOrDefault(jingle.DurationSeconds) - jingle.StartSeconds),
                    OriginalOrder = originalOrder++,
                    IsIncluded = jingleIds.Contains(jingle.Id),
                    IsWholeDeckIncluded = deckEditor.IncludeWholeDeck,
                };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(RandomPlayerJingleEditor.IsIncluded)) RefreshOverview();
                };
                deckEditor.Jingles.Add(item);
            }
            deckEditor.SelectionChanged = RefreshOverview;
            editor.Decks.Add(deckEditor);
        }
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RandomPlayerProfileEditor.Name) or nameof(RandomPlayerProfileEditor.Shortcut))
                RefreshOverview();
        };
        return editor;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchBox?.Clear();
        ApplySearch("");
        RefreshOverview();
    }

    private void SetupsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchBox?.Clear();
        ApplySearch("");
        RefreshOverview();
    }

    private void AddSetup_Click(object sender, RoutedEventArgs e)
    {
        var number = 1;
        string name;
        do name = $"Slumpprofil {number++}";
        while (ViewData.Setups.Any(setup => string.Equals(setup.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        var setup = CreateSetupEditor(new RandomPoolSetup { Name = name });
        ViewData.Setups.Add(setup);
        ViewData.SelectedSetup = setup;
    }

    private void DuplicateSetup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedSetup is not { } source) return;
        var baseName = $"{source.Name} – kopia";
        var name = baseName;
        var suffix = 2;
        while (ViewData.Setups.Any(setup => string.Equals(setup.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var setup = CreateSetupEditor(new RandomPoolSetup
        {
            Name = name,
            Profiles = source.Profiles.Select(ToModel).ToList()
        });
        ViewData.Setups.Add(setup);
        ViewData.SelectedSetup = setup;
    }

    private void RemoveSetup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedSetup is not { } selected || ViewData.Setups.Count <= 1) return;
        if (MessageBox.Show(this, $"Ta bort slumpprofilen ‘{selected.Name}’ och alla dess grupper?",
                "Ta bort slumpprofil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = ViewData.Setups.IndexOf(selected);
        ViewData.Setups.Remove(selected);
        ViewData.SelectedSetup = ViewData.Setups[Math.Clamp(index, 0, ViewData.Setups.Count - 1)];
    }

    private void DeckTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedSort();
        RefreshOverview();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySelectedSort();

    private void ApplySelectedSort()
    {
        if (DeckTabs is null || SortCombo is null) return;
        if (DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck ||
            SortCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;

        IEnumerable<RandomPlayerJingleEditor> sorted = mode switch
        {
            "asc" => deck.Jingles.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.OriginalOrder),
            "desc" => deck.Jingles.OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.OriginalOrder),
            _ => deck.Jingles.OrderBy(item => item.OriginalOrder)
        };
        var ordered = sorted.ToArray();
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = deck.Jingles.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex) deck.Jingles.Move(currentIndex, targetIndex);
        }
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var index = 1;
        string name;
        do name = $"Slumpgrupp {index++}";
        while (ViewData.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        var profile = CreateEditor(new RandomPoolProfile { Name = name });
        ViewData.Profiles.Add(profile);
        ViewData.SelectedProfile = profile;
        ProfilesList.ScrollIntoView(profile);
        RefreshOverview();
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } source) return;
        var baseName = $"{source.Name} – kopia";
        var name = baseName;
        var suffix = 2;
        while (ViewData.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var copy = CreateEditor(new RandomPoolProfile
        {
            Name = name,
            DeckIds = source.Decks.Where(deck => deck.IncludeWholeDeck).Select(deck => deck.DeckId).ToList(),
            JingleIds = source.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.IsIncluded).Select(jingle => jingle.JingleId).ToList()
        });
        ViewData.Profiles.Add(copy);
        ViewData.SelectedProfile = copy;
        ProfilesList.ScrollIntoView(copy);
        RefreshOverview();
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } selected) return;
        if (ViewData.Profiles.Count > 1 && MessageBox.Show(this,
                $"Ta bort slumpgruppen ‘{selected.Name}’?", "Ta bort slumpgrupp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = ViewData.Profiles.IndexOf(selected);
        ViewData.Profiles.Remove(selected);
        if (ViewData.Profiles.Count == 0) ViewData.Profiles.Add(CreateEditor(new RandomPoolProfile { Name = "Slumpgrupp 1" }));
        ViewData.SelectedProfile = ViewData.Profiles[Math.Clamp(index, 0, ViewData.Profiles.Count - 1)];
        RefreshOverview();
    }

    private void ChooseShortcut_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewData.SelectedProfile;
        if (selected is null) return;
        var dialog = new ShortcutCaptureWindow(selected.Shortcut) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var shortcut = ShortcutService.Normalize(dialog.SelectedShortcut);
        if (!ConfirmShortcutReplacement(selected, shortcut)) return;
        selected.Shortcut = shortcut;
        RefreshOverview();
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is { } selected) selected.Shortcut = null;
        RefreshOverview();
    }

    private bool ConfirmShortcutReplacement(RandomPlayerProfileEditor selected, string? shortcut)
    {
        if (shortcut is null) return true;
        var otherProfiles = ViewData.Profiles.Where(profile => profile != selected && SameShortcut(profile.Shortcut, shortcut)).ToArray();
        var jingles = _project.Decks.SelectMany(deck => deck.Jingles)
            .Where(jingle => SameShortcut(jingle.Shortcut, shortcut) || SameShortcut(jingle.CategoryShortcut, shortcut)).ToArray();
        var teams = (_project.Settings.TeamDeckProfiles ?? []).Where(team => SameShortcut(team.Shortcut, shortcut)).ToArray();
        var autoplay = (_project.Settings.AutoplayProfiles ?? []).Where(profile => SameShortcut(profile.Shortcut, shortcut)).ToArray();
        if (otherProfiles.Length == 0 && jingles.Length == 0 && teams.Length == 0 && autoplay.Length == 0) return true;

        var owners = otherProfiles.Select(profile => $"slumpgruppen ‘{profile.Name}’")
            .Concat(jingles.Select(jingle => $"jinglen ‘{jingle.Title}’"))
            .Concat(teams.Select(team => $"Team Deck ‘{team.Name}’"))
            .Concat(autoplay.Select(profile => $"Autoplay-listan ‘{profile.Name}’"))
            .Distinct().Take(6);
        if (MessageBox.Show(this,
                $"Snabbtangenten {shortcut} används redan av {string.Join(", ", owners)}.\n\nVill du flytta tangenten till ‘{selected.Name}’?",
                "Snabbtangenten används redan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        foreach (var profile in otherProfiles) profile.Shortcut = null;
        _replacementShortcuts.Add(shortcut);
        return true;
    }

    private static bool SameShortcut(string? first, string? second) =>
        string.Equals(ShortcutService.Normalize(first), ShortcutService.Normalize(second), StringComparison.OrdinalIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch(SearchBox.Text);

    private void ApplySearch(string? query)
    {
        if (ViewData.SelectedProfile is not { } profile) return;
        var needle = query?.Trim() ?? "";
        var normalizedNeedle = NormalizeSearchText(needle);
        var tokens = normalizedNeedle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RandomPlayerDeckEditor? bestDeck = null;
        var bestScore = int.MaxValue;
        foreach (var deck in profile.Decks)
        {
            foreach (var jingle in deck.Jingles)
            {
                var candidate = NormalizeSearchText(jingle.SearchText);
                jingle.IsVisible = tokens.Length == 0 || tokens.All(candidate.Contains);
                if (!jingle.IsVisible || tokens.Length == 0) continue;
                var score = SearchScore(candidate, normalizedNeedle, tokens);
                if (score >= bestScore) continue;
                bestScore = score;
                bestDeck = deck;
            }
            deck.RefreshCounts();
        }
        // Sökningen gäller hela profilen. Användaren behöver därför aldrig först
        // gissa vilket deck låten ligger i; bästa träffen öppnar rätt flik direkt.
        if (bestDeck is not null && !ReferenceEquals(DeckTabs.SelectedItem, bestDeck))
        {
            DeckTabs.SelectedItem = bestDeck;
            DeckTabs.UpdateLayout();
        }
    }

    private static int SearchScore(string candidate, string fullQuery, IReadOnlyList<string> tokens)
    {
        if (candidate.Equals(fullQuery, StringComparison.Ordinal)) return 0;
        if (candidate.StartsWith(fullQuery, StringComparison.Ordinal)) return 10;
        var fullIndex = candidate.IndexOf(fullQuery, StringComparison.Ordinal);
        if (fullIndex >= 0) return 20 + fullIndex;
        return 100 + tokens.Sum(token => Math.Max(0, candidate.IndexOf(token, StringComparison.Ordinal)));
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck) return;
        foreach (var jingle in deck.Jingles.Where(jingle => jingle.IsVisible)) jingle.IsIncluded = true;
        RefreshOverview();
    }

    private void ClearSelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        if (DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck) return;
        deck.IncludeWholeDeck = false;
        foreach (var jingle in deck.Jingles) jingle.IsIncluded = false;
        RefreshOverview();
    }

    private void PoolSelection_Click(object sender, RoutedEventArgs e) => RefreshOverview();

    private void RefreshOverview()
    {
        if (!IsInitialized || ViewData is null) return;
        ViewData.PoolItems.Clear();
        var selectedSounds = 0;
        var selectedDecks = 0;
        if (ViewData.SelectedProfile is { } selected)
        {
            foreach (var deck in selected.Decks)
            {
                deck.RefreshCounts();
                var included = deck.Jingles.Where(item => deck.IncludeWholeDeck || item.IsIncluded).ToArray();
                if (included.Length > 0) selectedDecks++;
                selectedSounds += included.Length;
                foreach (var item in included)
                    ViewData.PoolItems.Add(new RandomPoolOverviewItem(item.Title, deck.Name,
                        deck.IncludeWholeDeck ? "HELA DECKET" : "VALD"));
            }
            selected.RefreshSummary();
        }
        SelectedSoundsText.Text = selectedSounds.ToString();
        SelectedDecksText.Text = selectedDecks.ToString();
        var total = ViewData.Profiles.Sum(profile => profile.SelectedSoundCount);
        HeaderSummaryText.Text = LanguageService.IsEnglish
            ? $"{ViewData.Setups.Count} {(ViewData.Setups.Count == 1 ? "profile" : "profiles")} • {ViewData.Profiles.Count} {(ViewData.Profiles.Count == 1 ? "group" : "groups")} • {total} selected sounds"
            : $"{ViewData.Setups.Count} {(ViewData.Setups.Count == 1 ? "profil" : "profiler")} • {ViewData.Profiles.Count} {(ViewData.Profiles.Count == 1 ? "grupp" : "grupper")} • {total} valda ljud";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var duplicate = ViewData.Setups.SelectMany(setup => setup.Profiles
                .Where(profile => profile.Shortcut is not null)
                .GroupBy(profile => ShortcutService.Normalize(profile.Shortcut), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => (Setup: setup, Group: group)))
            .FirstOrDefault();
        if (duplicate.Group is not null)
        {
            MessageBox.Show(this, $"Snabbtangenten {duplicate.Group.Key} används av flera slumpgrupper i ‘{duplicate.Setup.Name}’.",
                "Dubblett av snabbtangent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var setup in ViewData.Setups)
        {
            if (string.IsNullOrWhiteSpace(setup.Name)) setup.Name = "Slumpprofil";
            foreach (var profile in setup.Profiles)
                if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "Slumpgrupp";
        }

        _project.Settings.RandomPoolSetups = ViewData.Setups.Select(setup => new RandomPoolSetup
        {
            Id = setup.Id,
            Name = setup.Name.Trim(),
            Profiles = setup.Profiles.Select(ToModel).ToList()
        }).ToList();
        _project.Settings.ActiveRandomPoolSetupId = ViewData.SelectedSetup?.Id
                                                    ?? _project.Settings.RandomPoolSetups[0].Id;
        _project.Settings.RandomPoolProfiles = _project.Settings.RandomPoolSetups
            .First(setup => setup.Id == _project.Settings.ActiveRandomPoolSetupId).Profiles;
        _project.Settings.RandomPoolShortcut = null;
        _project.Settings.RandomPoolDeckIds = [];
        _project.Settings.RandomPoolJingleIds = [];

        foreach (var shortcut in _replacementShortcuts)
        {
            foreach (var jingle in _project.Decks.SelectMany(deck => deck.Jingles))
            {
                if (SameShortcut(jingle.Shortcut, shortcut)) jingle.Shortcut = null;
                if (SameShortcut(jingle.CategoryShortcut, shortcut)) jingle.CategoryShortcut = null;
            }
            foreach (var team in _project.Settings.TeamDeckProfiles ?? [])
                if (SameShortcut(team.Shortcut, shortcut)) team.Shortcut = null;
            foreach (var autoplay in _project.Settings.AutoplayProfiles ?? [])
                if (SameShortcut(autoplay.Shortcut, shortcut)) autoplay.Shortcut = null;
        }
        DialogResult = true;
    }

    private static RandomPoolProfile ToModel(RandomPlayerProfileEditor profile) => new()
    {
        Id = profile.Id,
        Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim(),
        Shortcut = ShortcutService.Normalize(profile.Shortcut),
        DeckIds = profile.Decks.Where(deck => deck.IncludeWholeDeck).Select(deck => deck.DeckId).Distinct().ToList(),
        JingleIds = profile.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.IsIncluded)
            .Select(jingle => jingle.JingleId).Distinct().ToList()
    };
}

public sealed class RandomPlayerSettingsViewData : INotifyPropertyChanged
{
    private RandomPlayerSetupEditor? _selectedSetup;
    private RandomPlayerProfileEditor? _selectedProfile;
    public required ObservableCollection<RandomPlayerSetupEditor> Setups { get; init; }
    public ObservableCollection<RandomPlayerProfileEditor> Profiles =>
        SelectedSetup?.Profiles ?? EmptyProfiles;
    private static ObservableCollection<RandomPlayerProfileEditor> EmptyProfiles { get; } = [];
    public ObservableCollection<RandomPoolOverviewItem> PoolItems { get; } = [];
    public RandomPlayerSetupEditor? SelectedSetup
    {
        get => _selectedSetup;
        set
        {
            if (ReferenceEquals(_selectedSetup, value)) return;
            _selectedSetup = value;
            Raise();
            Raise(nameof(Profiles));
            SelectedProfile = value?.Profiles.FirstOrDefault();
        }
    }
    public RandomPlayerProfileEditor? SelectedProfile
    {
        get => _selectedProfile;
        set { if (ReferenceEquals(_selectedProfile, value)) return; _selectedProfile = value; Raise(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RandomPlayerSetupEditor : INotifyPropertyChanged
{
    private string _name = "Slumpprofil";
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public required ObservableCollection<RandomPlayerProfileEditor> Profiles { get; init; }
    public string Summary => LanguageService.IsEnglish
        ? $"{Profiles.Count} {(Profiles.Count == 1 ? "group" : "groups")}" : $"{Profiles.Count} {(Profiles.Count == 1 ? "grupp" : "grupper")}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RandomPlayerProfileEditor : INotifyPropertyChanged
{
    private string _name = "Slumpgrupp";
    private string? _shortcut;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public string? Shortcut { get => _shortcut; set { if (_shortcut == value) return; _shortcut = value; Raise(); Raise(nameof(ShortcutDisplay)); } }
    public string ShortcutDisplay => ShortcutService.Normalize(Shortcut) ?? (LanguageService.IsEnglish ? "<None>" : "<Ingen>");
    public ObservableCollection<RandomPlayerDeckEditor> Decks { get; } = [];
    public int SelectedSoundCount => Decks.Sum(deck => deck.IncludeWholeDeck ? deck.Jingles.Count : deck.Jingles.Count(item => item.IsIncluded));
    public string SelectionSummary => LanguageService.IsEnglish ? $"{SelectedSoundCount} sounds" : $"{SelectedSoundCount} ljud";
    public void RefreshSummary() { Raise(nameof(SelectedSoundCount)); Raise(nameof(SelectionSummary)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RandomPlayerDeckEditor : INotifyPropertyChanged
{
    private bool _includeWholeDeck;
    public Guid DeckId { get; init; }
    public required string Name { get; init; }
    public bool IncludeWholeDeck
    {
        get => _includeWholeDeck;
        set
        {
            if (_includeWholeDeck == value) return;
            _includeWholeDeck = value;
            foreach (var jingle in Jingles) jingle.IsWholeDeckIncluded = value;
            Raise();
            RefreshCounts();
            SelectionChanged?.Invoke();
        }
    }
    public ObservableCollection<RandomPlayerJingleEditor> Jingles { get; } = [];
    public int SelectedCount => IncludeWholeDeck ? Jingles.Count : Jingles.Count(item => item.IsIncluded);
    public string MatchSummary => LanguageService.IsEnglish
        ? $"{Jingles.Count(item => item.IsVisible)} of {Jingles.Count} shown" : $"{Jingles.Count(item => item.IsVisible)} av {Jingles.Count} visas";
    public Action? SelectionChanged { get; set; }
    public void RefreshCounts() { Raise(nameof(SelectedCount)); Raise(nameof(MatchSummary)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RandomPlayerJingleEditor : INotifyPropertyChanged
{
    private bool _isIncluded;
    private bool _isWholeDeckIncluded;
    private bool _isVisible = true;
    public Guid JingleId { get; init; }
    public required string Title { get; init; }
    public int OriginalOrder { get; init; }
    public required string SearchText { get; init; }
    public double DurationSeconds { get; init; }
    public string DurationDisplay => TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            Raise();
            Raise(nameof(DisplayIsIncluded));
        }
    }
    public bool IsWholeDeckIncluded
    {
        get => _isWholeDeckIncluded;
        set
        {
            if (_isWholeDeckIncluded == value) return;
            _isWholeDeckIncluded = value;
            Raise();
            Raise(nameof(DisplayIsIncluded));
            Raise(nameof(CanToggleIndividually));
        }
    }
    public bool DisplayIsIncluded
    {
        get => IsWholeDeckIncluded || IsIncluded;
        set { if (!IsWholeDeckIncluded) IsIncluded = value; }
    }
    public bool CanToggleIndividually => !IsWholeDeckIncluded;
    public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; Raise(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RandomPoolOverviewItem(string Title, string DeckName, string SourceLabel);
