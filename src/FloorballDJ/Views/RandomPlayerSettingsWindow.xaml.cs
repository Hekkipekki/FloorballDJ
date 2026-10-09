using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class RandomPlayerSettingsWindow : Window
{
    private readonly FloorballProject _project;
    private string _sortMode = "deck";
    private int _fileProbeCount, _jingleEditorCount, _overviewCount;
    internal (int FileProbes, int JingleEditors, int Overviews) Work => (_fileProbeCount, _jingleEditorCount, _overviewCount);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RandomSettingsPreparation _preparation;
    private readonly Func<bool> _isCurrentProject;
    private readonly RandomSettingsLibraryInput _input;
    private readonly RandomPoolSetup[] _setupInput;
    private readonly Guid? _activeSetupInput;
    private readonly PerformanceOperation _opening;
    private readonly long _openedAt;
    private RandomSettingsLibrary? _library;
    private DispatcherOperation? _overviewPending;
    private bool _closed;
    public RandomPlayerSettingsViewData ViewData { get; private set; } = new() { Setups = [] };
    internal Task<bool> Initialization { get; } = Task.FromResult(false);

    public RandomPlayerSettingsWindow(FloorballProject project, Func<bool>? isCurrentProject = null)
        : this(project, isCurrentProject, RandomSettingsPreparation.Shared) { }

    internal RandomPlayerSettingsWindow(FloorballProject project, Func<bool>? isCurrentProject, RandomSettingsPreparation preparation, bool configurePlacement = true)
    {
        _opening = PerformanceDiagnostics.BeginOperation("RandomSettingsOpenRequested");
        _openedAt = _opening.Timestamp;
        using (_opening.Measure("RandomSettingsXamlConstruction")) InitializeComponent();
        // The internal switch is an analysis-only ablation. Public callers keep
        // the existing owner-monitor placement and maximizing behavior.
        if (configurePlacement) WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _project = project;
        _preparation = preparation;
        _isCurrentProject = isCurrentProject ?? (() => true);
        using (_opening.Measure("RandomSettingsLayoutNormalization")) ProjectService.EnsureLayout(project);
        _input = RandomSettingsLibraryInput.Capture(project);
        _activeSetupInput = project.Settings.ActiveRandomPoolSetupId;
        _setupInput = project.Settings.RandomPoolSetups.Select(setup => new RandomPoolSetup
            { Id = setup.Id, Name = setup.Name, Profiles = setup.Profiles.Select(CopyProfile).ToList() }).ToArray();
        DataContext = ViewData;
        HeaderSummaryText.Text = LanguageService.Translate("Förbereder…");
        ContentRendered += (_, _) => _opening.Duration("RandomSettingsFirstContentRendered", _openedAt);
        if (_opening.Enabled)
        {
            Loaded += (_, _) => _opening.Mark("RandomSettingsWindowLoaded");
            Activated += (_, _) => _opening.Mark("RandomSettingsWindowActivated");
            Deactivated += (_, _) => _opening.Mark("RandomSettingsWindowDeactivated");
        }
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _overviewPending?.Abort(); if (Initialization.IsCompleted) _lifetime.Dispose(); };
        Initialization = InitializeAsync();
    }

    private async Task<bool> InitializeAsync()
    {
        try
        {
            // Let the initial window/cancel button render before doing preparation.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var library = await _preparation.PrepareAsync(_input, _lifetime.Token, _opening);
            if (_closed || !_isCurrentProject() || !_input.Matches(_project) || !SettingsMatch())
            {
                _opening.Mark("RandomSettingsStale");
                if (!_closed) HeaderSummaryText.Text = LanguageService.IsEnglish ? "The profile changed. Reopen the window." : "Profilen har ändrats. Öppna fönstret igen.";
                return false;
            }
            _library = library;
            _fileProbeCount += library.FileChecks;
            using (_opening.Measure("RandomSettingsDraftConstruction"))
            {
                var setups = new ObservableCollection<RandomPlayerSetupEditor>(_setupInput.Select(CreateSetupEditor));
                if (setups.Count == 0) setups.Add(CreateSetupEditor(new RandomPoolSetup { Name = "Standard" }));
                ViewData = new RandomPlayerSettingsViewData { Setups = setups, IsReady = true };
                ViewData.SelectedSetup = setups.FirstOrDefault(setup => setup.Id == _activeSetupInput) ?? setups[0];
            }
            using (_opening.Measure("RandomSettingsBindingApply")) DataContext = ViewData;
            RequestOverview();
            _opening.Duration("RandomSettingsDraftReady", _openedAt);
            _opening.Mark("RandomSettingsSetupCount", ViewData.Setups.Count);
            _opening.Mark("RandomSettingsGroupCount", ViewData.Setups.Sum(setup => setup.Profiles.Count));
            await Dispatcher.InvokeAsync(() => { if (!_closed) _opening.Duration("RandomSettingsReady", _openedAt); }, DispatcherPriority.ContextIdle);
            return !_closed;
        }
        catch (OperationCanceledException) { _opening.Mark("RandomSettingsCancelled"); return false; }
        catch (Exception exception)
        {
            _opening.Mark("RandomSettingsFailed", detail: exception.GetType().Name);
            if (!_closed)
            {
                ViewData.IsReady = false;
                HeaderSummaryText.Text = LanguageService.IsEnglish ? "Could not prepare the song list. Reopen the window." : "Kunde inte förbereda låtlistan. Öppna fönstret igen.";
            }
            return false;
        }
        finally { if (_closed) _lifetime.Dispose(); }
    }

    private static RandomPoolProfile CopyProfile(RandomPoolProfile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, Shortcut = profile.Shortcut,
        DeckIds = profile.DeckIds.ToList(), JingleIds = profile.JingleIds.ToList(), FollowUpJingleIds = profile.FollowUpJingleIds.ToList(),
        FollowUpFadeInSeconds = profile.FollowUpFadeInSeconds, FollowUpFadeOutSeconds = profile.FollowUpFadeOutSeconds,
        DeckVariationEnabled = profile.DeckVariationEnabled, MaxConsecutiveFromSameDeck = profile.MaxConsecutiveFromSameDeck
    };

    private bool SettingsMatch()
    {
        if (_project.Settings.ActiveRandomPoolSetupId != _activeSetupInput || _project.Settings.RandomPoolSetups.Count != _setupInput.Length) return false;
        for (var s = 0; s < _setupInput.Length; s++)
        {
            var source = _setupInput[s]; var current = _project.Settings.RandomPoolSetups[s];
            if (source.Id != current.Id || source.Name != current.Name || source.Profiles.Count != current.Profiles.Count) return false;
            for (var p = 0; p < source.Profiles.Count; p++)
            {
                var a = source.Profiles[p]; var b = current.Profiles[p];
                if (a.Id != b.Id || a.Name != b.Name || a.Shortcut != b.Shortcut || !a.DeckIds.SequenceEqual(b.DeckIds) ||
                    !a.JingleIds.SequenceEqual(b.JingleIds) || !a.FollowUpJingleIds.SequenceEqual(b.FollowUpJingleIds) ||
                    !a.FollowUpFadeInSeconds.Equals(b.FollowUpFadeInSeconds) || !a.FollowUpFadeOutSeconds.Equals(b.FollowUpFadeOutSeconds) ||
                    a.DeckVariationEnabled != b.DeckVariationEnabled || a.MaxConsecutiveFromSameDeck != b.MaxConsecutiveFromSameDeck) return false;
            }
        }
        return true;
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
            Shortcut = ShortcutService.Normalize(profile.Shortcut),
            FollowUpJingleIds = profile.FollowUpJingleIds?.Distinct().ToList() ?? [],
            FollowUpFadeOutSeconds = profile.FollowUpFadeOutSeconds,
            FollowUpFadeInSeconds = profile.FollowUpFadeInSeconds,
            DeckVariationEnabled = profile.DeckVariationEnabled,
            MaxConsecutiveFromSameDeck = profile.MaxConsecutiveFromSameDeck
        };
        var library = _library ?? throw new InvalidOperationException("Library preparation is not ready.");
        editor.PrepareDecks(library, deckIds, jingleIds, () => CreateDeckEditors(library, deckIds, jingleIds));
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RandomPlayerProfileEditor.Name) or nameof(RandomPlayerProfileEditor.Shortcut)) RequestOverview();
        };
        return editor;
    }

    private ObservableCollection<RandomPlayerDeckEditor> CreateDeckEditors(RandomSettingsLibrary library, HashSet<Guid> deckIds, HashSet<Guid> jingleIds)
    {
        var operation = _opening;
        operation.Mark("RandomSettingsGroupRealizationRequested");
        using var duration = operation.Measure("RandomSettingsGroupRealization");
        var decks = new ObservableCollection<RandomPlayerDeckEditor>();
        var created = 0;
        foreach (var deck in library.Decks)
        {
            var deckEditor = new RandomPlayerDeckEditor
            {
                DeckId = deck.Id,
                Name = deck.Name,
                IncludeWholeDeck = deckIds.Contains(deck.Id)
            };
            foreach (var jingle in deck.Jingles)
            {
                _jingleEditorCount++;
                created++;
                var item = new RandomPlayerJingleEditor
                {
                    JingleId = jingle.Id, Title = jingle.Title, SearchText = jingle.SearchText,
                    NormalizedSearchText = jingle.NormalizedSearchText, DurationSeconds = jingle.Duration, OriginalOrder = jingle.Order,
                    IsIncluded = jingleIds.Contains(jingle.Id),
                    IsWholeDeckIncluded = deckEditor.IncludeWholeDeck,
                };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(RandomPlayerJingleEditor.IsIncluded)) RequestOverview();
                };
                deckEditor.Jingles.Add(item);
            }
            deckEditor.SelectionChanged = RequestOverview;
            decks.Add(deckEditor);
        }
        operation.Mark("RandomSettingsJingleEditors", created);
        return decks;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchBox?.Clear();
        ApplySearch("");
        RequestOverview();
    }

    private void SetupsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchBox?.Clear();
        ApplySearch("");
        RequestOverview();
    }

    private async void AddSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!await PrepareFreshLibraryAsync()) return;
        var number = 1;
        string name;
        do name = $"Slumpprofil {number++}";
        while (ViewData.Setups.Any(setup => string.Equals(setup.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        var setup = CreateSetupEditor(new RandomPoolSetup { Name = name });
        ViewData.Setups.Add(setup);
        ViewData.SelectedSetup = setup;
    }

    private void RenameSetup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedSetup is not { } selected) return;
        var dialog = new TextPromptWindow("Byt namn på slumpprofil", "Namn på slumpprofil",
            "Namnet visas i väljaren och påverkar inte musikprofilen.", selected.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        selected.Name = dialog.Value;
        RequestOverview();
    }

    private async void DuplicateSetup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedSetup is not { } source) return;
        if (!await PrepareFreshLibraryAsync() || !ReferenceEquals(ViewData.SelectedSetup, source)) return;
        var baseName = $"{source.Name} – kopia";
        var name = baseName;
        var suffix = 2;
        while (ViewData.Setups.Any(setup => string.Equals(setup.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var setup = CreateSetupEditor(new RandomPoolSetup
        {
            Name = name,
            Profiles = source.Profiles.Select(profile => { var copy = ToModel(profile); copy.Id = Guid.NewGuid(); return copy; }).ToList()
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

    private void DeckTabs_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        // Preparation and group/setup changes can populate an already-loaded
        // TabControl. Wait for the ItemsSource binding transfer, then restore
        // the first deck only when there is no valid existing selection.
        if (e.Property == ItemsControl.ItemsSourceProperty && !_closed && ViewData.IsReady &&
            DeckTabs.SelectedIndex < 0 && DeckTabs.Items.Count > 0) DeckTabs.SelectedIndex = 0;
    }

    private void DeckTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedSort();
        RequestOverview();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode }) _sortMode = mode;
        ApplySelectedSort();
    }

    private void ApplySelectedSort()
    {
        if (DeckTabs is null || DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck) return;

        IEnumerable<RandomPlayerJingleEditor> sorted = _sortMode switch
        {
            "asc" => deck.Jingles.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.OriginalOrder),
            "desc" => deck.Jingles.OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.OriginalOrder),
            _ => deck.Jingles.OrderBy(item => item.OriginalOrder)
        };
        var ordered = sorted.ToArray();
        deck.ApplyOrder(ordered);
    }

    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var setup = ViewData.SelectedSetup;
        if (!await PrepareFreshLibraryAsync() || !ReferenceEquals(ViewData.SelectedSetup, setup)) return;
        var index = 1;
        string name;
        do name = $"Slumpgrupp {index++}";
        while (ViewData.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        var profile = CreateEditor(new RandomPoolProfile { Name = name });
        ViewData.Profiles.Add(profile);
        ViewData.SelectedProfile = profile;
        ProfilesList.ScrollIntoView(profile);
        RequestOverview();
    }

    private async void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } source) return;
        if (!await PrepareFreshLibraryAsync() || !ReferenceEquals(ViewData.SelectedProfile, source)) return;
        var baseName = $"{source.Name} – kopia";
        var name = baseName;
        var suffix = 2;
        while (ViewData.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var copy = CreateEditor(new RandomPoolProfile
        {
            Name = name,
            DeckIds = source.Decks.Where(deck => deck.IncludeWholeDeck).Select(deck => deck.DeckId).ToList(),
            JingleIds = source.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.IsIncluded).Select(jingle => jingle.JingleId).ToList(),
            FollowUpJingleIds = source.FollowUpJingleIds.ToList(),
            FollowUpFadeOutSeconds = source.FollowUpFadeOutSeconds,
            FollowUpFadeInSeconds = source.FollowUpFadeInSeconds,
            DeckVariationEnabled = source.DeckVariationEnabled,
            MaxConsecutiveFromSameDeck = source.MaxConsecutiveFromSameDeck
        });
        ViewData.Profiles.Add(copy);
        ViewData.SelectedProfile = copy;
        ProfilesList.ScrollIntoView(copy);
        RequestOverview();
    }

    private async void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } selected) return;
        if (ViewData.Profiles.Count > 1 && MessageBox.Show(this,
                $"Ta bort slumpgruppen ‘{selected.Name}’?", "Ta bort slumpgrupp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = ViewData.Profiles.IndexOf(selected);
        if (ViewData.Profiles.Count == 1 && !await PrepareFreshLibraryAsync()) return;
        if (!ReferenceEquals(ViewData.SelectedProfile, selected)) return;
        ViewData.Profiles.Remove(selected);
        if (ViewData.Profiles.Count == 0) ViewData.Profiles.Add(CreateEditor(new RandomPoolProfile { Name = "Slumpgrupp 1" }));
        ViewData.SelectedProfile = ViewData.Profiles[Math.Clamp(index, 0, ViewData.Profiles.Count - 1)];
        RequestOverview();
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
        RequestOverview();
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is { } selected) selected.Shortcut = null;
        RequestOverview();
    }

    private void ChooseFollowUps_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } selected) return;
        var dialog = new RandomFollowUpPickerWindow(_project, selected.FollowUpJingleIds) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        selected.FollowUpJingleIds = dialog.SelectedJingleIds.ToList();
        selected.RefreshSummary();
    }

    private void ClearFollowUps_Click(object sender, RoutedEventArgs e)
    {
        if (ViewData.SelectedProfile is not { } selected) return;
        selected.FollowUpJingleIds = [];
        selected.RefreshSummary();
    }

    private bool ConfirmShortcutReplacement(RandomPlayerProfileEditor selected, string? shortcut)
    {
        if (shortcut is null) return true;
        var otherProfiles = ViewData.Profiles.Where(profile => profile != selected && SameShortcut(profile.Shortcut, shortcut)).ToArray();
        if (otherProfiles.Length == 0) return true;

        var owners = otherProfiles.Select(profile => $"slumpgruppen ‘{profile.Name}’")
            .Distinct().Take(6);
        if (MessageBox.Show(this,
                $"Snabbtangenten {shortcut} används redan av {string.Join(", ", owners)}.\n\nVill du flytta tangenten till ‘{selected.Name}’?",
                "Snabbtangenten används redan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        foreach (var profile in otherProfiles) profile.Shortcut = null;
        return true;
    }

    private static bool SameShortcut(string? first, string? second) =>
        string.Equals(ShortcutService.Normalize(first), ShortcutService.Normalize(second), StringComparison.OrdinalIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch(SearchBox.Text);

    private void ApplySearch(string? query)
    {
        if (_closed || !ViewData.IsReady) return;
        if (ViewData.SelectedProfile is not { } profile) return;
        var needle = query?.Trim() ?? "";
        var normalizedNeedle = NormalizeSearchText(needle);
        var tokens = normalizedNeedle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RandomPlayerDeckEditor? bestDeck = null;
        var bestScore = int.MaxValue;
        foreach (var deck in profile.Decks)
        {
            var visibilityChanged = false;
            foreach (var jingle in deck.Jingles)
            {
                var candidate = jingle.NormalizedSearchText ?? NormalizeSearchText(jingle.SearchText);
                var visible = tokens.Length == 0 || tokens.All(candidate.Contains);
                visibilityChanged |= jingle.IsVisible != visible;
                jingle.IsVisible = visible;
                if (!jingle.IsVisible || tokens.Length == 0) continue;
                var score = SearchScore(candidate, normalizedNeedle, tokens);
                if (score >= bestScore) continue;
                bestScore = score;
                bestDeck = deck;
            }
            // Source additions/removals and sorting update the view themselves.
            // Refresh only changed filter membership; ranking still runs above.
            if (visibilityChanged) deck.VisibleJingles.Refresh();
            deck.RefreshCounts();
        }
        // Sökningen gäller hela profilen. Användaren behöver därför aldrig först
        // gissa vilket deck låten ligger i; bästa träffen öppnar rätt flik direkt.
        if (bestDeck is not null && !ReferenceEquals(DeckTabs.SelectedItem, bestDeck))
        {
            DeckTabs.SelectedItem = bestDeck;
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

    private static string NormalizeSearchText(string? value) => RandomSettingsPreparation.NormalizeSearchText(value);

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck) return;
        foreach (var jingle in deck.Jingles.Where(jingle => jingle.IsVisible)) jingle.IsIncluded = true;
        RequestOverview();
    }

    private void ClearSelectedDeck_Click(object sender, RoutedEventArgs e)
    {
        if (DeckTabs.SelectedItem is not RandomPlayerDeckEditor deck) return;
        deck.IncludeWholeDeck = false;
        foreach (var jingle in deck.Jingles) jingle.IsIncluded = false;
        RequestOverview();
    }

    private void PoolSelection_Click(object sender, RoutedEventArgs e) => RequestOverview();

    private void RequestOverview()
    {
        if (_closed || !ViewData.IsReady || _overviewPending?.Status == DispatcherOperationStatus.Pending) return;
        _overviewPending = Dispatcher.InvokeAsync(() => { _overviewPending = null; RefreshOverview(); }, DispatcherPriority.Background);
    }

    private async Task<bool> PrepareFreshLibraryAsync()
    {
        if (_closed || !ViewData.IsReady) return false;
        ViewData.IsReady = false;
        try
        {
            var input = RandomSettingsLibraryInput.Capture(_project);
            var library = await _preparation.PrepareAsync(input, _lifetime.Token, _opening);
            if (_closed || !_isCurrentProject() || !_input.Matches(_project) || !input.Matches(_project) || !SettingsMatch())
            {
                _opening.Mark("RandomSettingsStale");
                if (!_closed) HeaderSummaryText.Text = LanguageService.IsEnglish ? "The profile changed. Reopen the window." : "Profilen har ändrats. Öppna fönstret igen.";
                return false;
            }
            _library = library;
            _fileProbeCount += library.FileChecks;
            return true;
        }
        catch (OperationCanceledException) { _opening.Mark("RandomSettingsCancelled"); return false; }
        catch (Exception exception)
        {
            _opening.Mark("RandomSettingsFailed", detail: exception.GetType().Name);
            if (!_closed) HeaderSummaryText.Text = LanguageService.IsEnglish ? "Could not prepare the song list. Reopen the window." : "Kunde inte förbereda låtlistan. Öppna fönstret igen.";
            return false;
        }
        finally
        {
            if (!_closed && _isCurrentProject() && _input.Matches(_project) && SettingsMatch()) { ViewData.IsReady = true; RequestOverview(); }
        }
    }

    private void RefreshOverview()
    {
        if (_closed || !IsInitialized || !ViewData.IsReady) return;
        _opening.Mark("RandomSettingsOverviewRequested");
        using var duration = _opening.Measure("RandomSettingsOverview");
        _overviewCount++;
        ViewData.PoolItems.Clear();
        var selectedSounds = 0;
        var selectedDecks = 0;
        var deckDistribution = new List<(string Name, int Count)>();
        if (ViewData.SelectedProfile is { } selected)
        {
            foreach (var deck in selected.Decks)
            {
                deck.RefreshCounts();
                var included = deck.SelectedCount;
                if (included > 0) selectedDecks++;
                selectedSounds += included;
                if (included > 0) deckDistribution.Add((deck.Name, included));
            }
            selected.RefreshSummary();
        }
        foreach (var deck in deckDistribution.OrderByDescending(item => item.Count)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var percentage = selectedSounds == 0 ? 0 : deck.Count * 100d / selectedSounds;
            ViewData.PoolItems.Add(new RandomPoolOverviewItem(deck.Name, deck.Count, percentage));
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
        if (TryApplyDraft()) DialogResult = true;
    }

    internal bool TryApplyDraft()
    {
        if (_closed || !ViewData.IsReady) return false;
        if (!_isCurrentProject() || !_input.Matches(_project) || !SettingsMatch())
        {
            HeaderSummaryText.Text = LanguageService.IsEnglish ? "The profile changed. Reopen the window." : "Profilen har ändrats. Öppna fönstret igen.";
            ViewData.IsReady = false;
            _opening.Mark("RandomSettingsStale");
            return false;
        }
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
            return false;
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

        return true;
    }

    private static RandomPoolProfile ToModel(RandomPlayerProfileEditor profile) => new()
    {
        Id = profile.Id,
        Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim(),
        Shortcut = ShortcutService.Normalize(profile.Shortcut),
        DeckIds = profile.SelectedDeckIds(),
        JingleIds = profile.SelectedJingleIds(),
        FollowUpJingleIds = profile.FollowUpJingleIds.Distinct().ToList(),
        FollowUpFadeOutSeconds = profile.FollowUpFadeOutSeconds,
        FollowUpFadeInSeconds = profile.FollowUpFadeInSeconds,
        DeckVariationEnabled = profile.DeckVariationEnabled,
        MaxConsecutiveFromSameDeck = Math.Clamp(profile.MaxConsecutiveFromSameDeck, 2, 10)
    };
}

public sealed class RandomPlayerSettingsViewData : INotifyPropertyChanged
{
    private RandomPlayerSetupEditor? _selectedSetup;
    private RandomPlayerProfileEditor? _selectedProfile;
    private bool _isReady;
    public bool IsReady { get => _isReady; internal set { if (_isReady == value) return; _isReady = value; Raise(); } }
    public required ObservableCollection<RandomPlayerSetupEditor> Setups { get; init; }
    public ObservableCollection<RandomPlayerProfileEditor> Profiles =>
        SelectedSetup?.Profiles ?? EmptyProfiles;
    private static ObservableCollection<RandomPlayerProfileEditor> EmptyProfiles { get; } = [];
    public ObservableCollection<RandomPoolOverviewItem> PoolItems { get; } = [];
    public IReadOnlyList<int> DeckVariationLimits { get; } = Enumerable.Range(2, 9).ToArray();
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
    private bool _deckVariationEnabled;
    private int _maxConsecutiveFromSameDeck = 4;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public string? Shortcut { get => _shortcut; set { if (_shortcut == value) return; _shortcut = value; Raise(); Raise(nameof(ShortcutDisplay)); } }
    public string ShortcutDisplay => ShortcutService.Normalize(Shortcut) ?? (LanguageService.IsEnglish ? "<None>" : "<Ingen>");
    private ObservableCollection<RandomPlayerDeckEditor>? _decks;
    private Func<ObservableCollection<RandomPlayerDeckEditor>>? _createDecks;
    private List<Guid> _preparedDeckIds = [], _preparedJingleIds = [];
    private int _preparedSelectedCount;
    public ObservableCollection<RandomPlayerDeckEditor> Decks
    {
        get
        {
            if (_decks is null) { _decks = _createDecks?.Invoke() ?? []; _createDecks = null; }
            return _decks;
        }
    }
    internal bool HasRealizedDecks => _decks is not null;
    internal void PrepareDecks(RandomSettingsLibrary library, HashSet<Guid> deckIds, HashSet<Guid> jingleIds,
        Func<ObservableCollection<RandomPlayerDeckEditor>> create)
    {
        _createDecks = create;
        foreach (var deck in library.Decks)
        {
            var whole = deckIds.Contains(deck.Id);
            if (whole) _preparedDeckIds.Add(deck.Id);
            foreach (var item in deck.Jingles)
            {
                var included = jingleIds.Contains(item.Id);
                if (included) _preparedJingleIds.Add(item.Id);
                if (whole || included) _preparedSelectedCount++;
            }
        }
    }
    internal List<Guid> SelectedDeckIds() => (_decks is null ? _preparedDeckIds : _decks.Where(deck => deck.IncludeWholeDeck).Select(deck => deck.DeckId)).Distinct().ToList();
    internal List<Guid> SelectedJingleIds() => (_decks is null ? _preparedJingleIds : _decks.SelectMany(deck => deck.Jingles).Where(item => item.IsIncluded).Select(item => item.JingleId)).Distinct().ToList();
    public List<Guid> FollowUpJingleIds { get; set; } = [];
    public double FollowUpFadeOutSeconds { get; set; } = 1.5;
    public double FollowUpFadeInSeconds { get; set; } = 0.75;
    public bool DeckVariationEnabled
    {
        get => _deckVariationEnabled;
        set { if (_deckVariationEnabled == value) return; _deckVariationEnabled = value; Raise(); }
    }
    public int MaxConsecutiveFromSameDeck
    {
        get => _maxConsecutiveFromSameDeck;
        set
        {
            var normalized = Math.Clamp(value, 2, 10);
            if (_maxConsecutiveFromSameDeck == normalized) return;
            _maxConsecutiveFromSameDeck = normalized;
            Raise();
        }
    }
    public string FollowUpSummary => FollowUpJingleIds.Count == 0
        ? (LanguageService.IsEnglish ? "No follow-up" : "Ingen följdlåt")
        : LanguageService.IsEnglish ? $"{FollowUpJingleIds.Count} selected" : $"{FollowUpJingleIds.Count} valda";
    public int SelectedSoundCount => _decks is null ? _preparedSelectedCount : _decks.Sum(deck => deck.SelectedCount);
    public string SelectionSummary => LanguageService.IsEnglish ? $"{SelectedSoundCount} sounds" : $"{SelectedSoundCount} ljud";
    public void RefreshSummary() { Raise(nameof(SelectedSoundCount)); Raise(nameof(SelectionSummary)); Raise(nameof(FollowUpSummary)); }
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
    private readonly OrderedJingles _jingles = new();
    public ObservableCollection<RandomPlayerJingleEditor> Jingles => _jingles;
    internal void ApplyOrder(RandomPlayerJingleEditor[] ordered) => _jingles.ApplyOrder(ordered);
    private sealed class OrderedJingles : ObservableCollection<RandomPlayerJingleEditor>
    {
        internal void ApplyOrder(RandomPlayerJingleEditor[] ordered)
        {
            if (this.SequenceEqual(ordered)) return;
            // Only the owning sort calls this with a permutation of these exact
            // editors. One Reset avoids N Move notifications and quadratic view
            // indexing, while keeping all identities and individual choices.
            for (var index = 0; index < ordered.Length; index++) Items[index] = ordered[index];
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
    // A private view keeps search out of the container generator without changing
    // the complete draft collection, its order, or offscreen individual choices.
    private ListCollectionView? _visibleJingles;
    public ListCollectionView VisibleJingles => _visibleJingles ??= new ListCollectionView(Jingles)
    {
        Filter = item => ((RandomPlayerJingleEditor)item).IsVisible
    };
    // The deck template can appear after the window's initial translation pass.
    public string WholeDeckLabel => LanguageService.Translate("Använd hela decket");
    public string WholeDeckHelp => LanguageService.Translate("Nya spelbara jinglar som senare läggs till i decket inkluderas automatiskt.");
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
    internal string? NormalizedSearchText { get; init; }
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

public sealed record RandomPoolOverviewItem(string DeckName, int SoundCount, double Percentage)
{
    public string CountText => LanguageService.IsEnglish
        ? $"{SoundCount} {(SoundCount == 1 ? "sound" : "sounds")}"
        : $"{SoundCount} ljud";
    public string PercentageText => $"{Percentage:0.#} %";
}
