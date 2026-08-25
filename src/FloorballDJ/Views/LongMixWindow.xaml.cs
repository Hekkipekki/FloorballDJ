using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using NAudio.Wave;

namespace FloorballDJ.Views;

public partial class LongMixWindow : Window
{
    public sealed record DeckTab(string Name, Deck? Deck);

    public sealed class LongMixTrack : INotifyPropertyChanged
    {
        private bool _isSelected;
        private MusicAnalysis? _analysis;
        private string _status = "Inte analyserad";
        private int _order;
        private string _matchFromPrevious = "";
        private double? _plannedTransitionSeconds;
        private double? _plannedEndSeconds;

        public required Deck Deck { get; init; }
        public required Jingle Jingle { get; init; }
        public string DeckName => Deck.Name;
        public string Title => Jingle.Title;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        public MusicAnalysis? Analysis { get => _analysis; set { if (Set(ref _analysis, value)) RaiseAnalysis(); } }
        public string Status { get => _status; set => Set(ref _status, value); }
        public int Order { get => _order; set => Set(ref _order, value); }
        public string MatchFromPrevious { get => _matchFromPrevious; set => Set(ref _matchFromPrevious, value); }
        public double? PlannedTransitionSeconds
        {
            get => _plannedTransitionSeconds;
            set { if (Set(ref _plannedTransitionSeconds, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnalysisSummary))); }
        }
        public double? PlannedEndSeconds
        {
            get => _plannedEndSeconds;
            set => Set(ref _plannedEndSeconds, value);
        }
        public string BpmText => Analysis is null ? "–" : $"{Analysis.Bpm:0.0}";
        public string KeyText => Analysis?.CamelotCode ?? "–";
        public string EnergyText => Analysis is null ? "–" : $"{Analysis.Energy:0}";
        public string AnalysisSummary => Analysis is null
            ? "Inte analyserad"
            : $"{Analysis.Bpm:0.0} BPM · {Analysis.CamelotCode} · energi {Analysis.Energy:0}" +
              (PlannedTransitionSeconds is { } point ? $" · växla {FormatShortTime(point - Analysis.SourceStartSeconds)}" : "");

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        private void RaiseAnalysis()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BpmText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnergyText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnalysisSummary)));
        }
        private static string FormatShortTime(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        }
    }

    private readonly MainViewModel _viewModel;
    private readonly MusicAnalysisService _analysisService = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ICollectionView _tracksView;
    private bool _working;
    private bool _updatingSuggestion;

    public ObservableCollection<DeckTab> DeckTabs { get; } = [];
    public ObservableCollection<LongMixTrack> Tracks { get; } = [];
    public ObservableCollection<LongMixTrack> SuggestedOrder { get; } = [];
    public ICollectionView TracksView => _tracksView;

    public LongMixWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _viewModel = viewModel;

        var nonEmptyDecks = viewModel.Decks
            .Where(deck => deck.Jingles.Any(jingle => jingle.HasAudio && File.Exists(jingle.FilePath)))
            .ToList();
        DeckTabs.Add(new DeckTab("Alla", null));
        foreach (var deck in nonEmptyDecks) DeckTabs.Add(new DeckTab(deck.Name, deck));
        foreach (var deck in nonEmptyDecks)
        foreach (var jingle in deck.Jingles.Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath)))
        {
            var track = new LongMixTrack
            {
                Deck = deck,
                Jingle = jingle,
                Analysis = jingle.HasFreshMusicAnalysis ? MusicAnalysisService.FromJingle(jingle) : null,
                Status = jingle.HasFreshMusicAnalysis ? "Sparad analys" : "Inte analyserad"
            };
            track.PropertyChanged += Track_PropertyChanged;
            Tracks.Add(track);
        }

        _tracksView = CollectionViewSource.GetDefaultView(Tracks);
        _tracksView.Filter = FilterTrack;
        DataContext = this;
        if (DeckTabs.Count > 0) DeckTabsControlSelectFirst();
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    private void DeckTabsControlSelectFirst() => DeckTabsControl.SelectedIndex = 0;

    private bool FilterTrack(object candidate)
    {
        if (candidate is not LongMixTrack track) return false;
        var search = SearchBox?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(search))
            return track.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                   track.DeckName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                   Path.GetFileName(track.Jingle.FilePath).Contains(search, StringComparison.CurrentCultureIgnoreCase);
        var tab = DeckTabsControl?.SelectedItem as DeckTab;
        return tab?.Deck is null || ReferenceEquals(tab.Deck, track.Deck);
    }

    private void DeckTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DeckTabsControl)) return;
        _tracksView?.Refresh();
        UpdateSelectCurrentLabel();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) && DeckTabsControl.SelectedIndex != 0)
            DeckTabsControl.SelectedIndex = 0;
        _tracksView?.Refresh();
        UpdateSelectCurrentLabel();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in _tracksView.Cast<LongMixTrack>()) track.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void SelectAllDecks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in Tracks) track.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in Tracks) track.IsSelected = false;
        SuggestedOrder.Clear();
        OpenEditorButton.IsEnabled = false;
        RefreshSuggestionSummary();
        UpdateSelectionStatus();
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LongMixTrack.IsSelected) || _working || _updatingSuggestion) return;
        SuggestedOrder.Clear();
        if (OpenEditorButton is not null) OpenEditorButton.IsEnabled = false;
        RefreshSuggestionSummary();
        if (StatusText is not null) UpdateSelectionStatus();
    }

    private async void AnalyzeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var selected = Tracks.Where(track => track.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Markera minst en låt som ska analyseras.";
            return;
        }
        await AnalyzeTracksAsync(selected);
    }

    private async Task<bool> AnalyzeTracksAsync(IReadOnlyList<LongMixTrack> tracks)
    {
        _working = true;
        AnalyzeButton.IsEnabled = SuggestButton.IsEnabled = false;
        Progress.Maximum = tracks.Count;
        Progress.Value = 0;
        var successful = 0;
        try
        {
            for (var index = 0; index < tracks.Count; index++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var track = tracks[index];
                StatusText.Text = $"Analyserar {index + 1} av {tracks.Count}: {track.Title}";
                track.Status = "Analyserar…";
                try
                {
                    if (track.Jingle.HasFreshMusicAnalysis)
                        track.Analysis = MusicAnalysisService.FromJingle(track.Jingle);
                    else
                    {
                        var end = track.Jingle.EndSeconds ?? GetDuration(track.Jingle.FilePath);
                        track.Analysis = await _analysisService.AnalyzeAsync(track.Jingle.FilePath,
                            track.Jingle.StartSeconds, end, _cancellation.Token);
                        MusicAnalysisService.ApplyToJingle(track.Jingle, track.Analysis);
                    }
                    track.Status = "Analyserad";
                    successful++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    track.Status = "Kunde inte analyseras";
                }
                Progress.Value = index + 1;
            }
            if (successful > 0) _viewModel.RequestSave();
            StatusText.Text = $"Analys klar: {successful} av {tracks.Count} låtar.";
            return successful == tracks.Count;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _working = false;
            AnalyzeButton.IsEnabled = SuggestButton.IsEnabled = true;
        }
    }

    private async void SuggestOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var selected = Tracks.Where(track => track.IsSelected).ToList();
        if (selected.Count < 2)
        {
            StatusText.Text = "Markera minst två låtar för att skapa en spelordning.";
            return;
        }
        if (!await AnalyzeTracksAsync(selected))
        {
            StatusText.Text = "Alla valda låtar behöver kunna analyseras innan en säker ordning kan föreslås.";
            return;
        }

        var ordered = LimitOrderToTarget(BuildBestOrder(selected), out var candidateCount);
        ordered[0].MatchFromPrevious = "START";
        for (var index = 1; index < ordered.Count; index++)
            ordered[index].MatchFromPrevious = $"{MusicAnalysisService.Compare(ordered[index - 1].Analysis!, ordered[index].Analysis!).Score} %";

        SuggestedOrder.Clear();
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Order = index + 1;
            SuggestedOrder.Add(ordered[index]);
        }
        RefreshSuggestedOrderMetadata();
        StatusText.Text = ordered.Count < candidateCount
            ? $"Spelordningen är klar. {ordered.Count} av {candidateCount} kandidater valdes för att passa önskad mixlängd."
            : "Spelordningen är klar. FloorballDJ har även valt den lämpligaste startlåten; finjustera övergångarna i nästa steg.";
    }

    private void RemoveSuggestedTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LongMixTrack track }) return;
        RemoveFromSuggestion(track);
    }

    private void RemoveWeakest_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestedOrder.Count < 2) return;
        var weakest = Enumerable.Range(1, SuggestedOrder.Count - 1)
            .Select(index => (Track: SuggestedOrder[index], Score: MusicAnalysisService.Compare(
                SuggestedOrder[index - 1].Analysis!, SuggestedOrder[index].Analysis!).Score))
            .OrderBy(item => item.Score)
            .First().Track;
        RemoveFromSuggestion(weakest);
    }

    private void RemoveFromSuggestion(LongMixTrack track)
    {
        _updatingSuggestion = true;
        try
        {
            track.IsSelected = false;
            SuggestedOrder.Remove(track);
            RefreshSuggestedOrderMetadata();
        }
        finally
        {
            _updatingSuggestion = false;
        }
        UpdateSelectionStatus();
        StatusText.Text = $"{track.Title} togs bort. Mixlängd och matchningar har räknats om.";
    }

    private void RefreshSuggestedOrderMetadata()
    {
        for (var index = 0; index < SuggestedOrder.Count; index++)
        {
            var track = SuggestedOrder[index];
            track.Order = index + 1;
            track.MatchFromPrevious = index == 0
                ? "START"
                : $"{MusicAnalysisService.Compare(SuggestedOrder[index - 1].Analysis!, track.Analysis!).Score} %";
        }
        PlanTransitionPoints();
        OpenEditorButton.IsEnabled = SuggestedOrder.Count >= 2;
        RefreshSuggestionSummary();
    }

    private void PlanTransitionPoints()
    {
        if (SuggestedOrder.Count == 0) return;
        var targetMinutes = ParsePlanningValue(TargetMinutesBox?.Text, 30, 1, 360);
        var minimum = ParsePlanningValue(MinTrackSecondsBox?.Text, 60, 10, 900);
        var maximum = ParsePlanningValue(MaxTrackSecondsBox?.Text, 150, minimum, 1800);
        var targetPerTrack = Math.Clamp(targetMinutes * 60 / SuggestedOrder.Count, minimum, maximum);

        for (var index = 0; index < SuggestedOrder.Count; index++)
        {
            var track = SuggestedOrder[index];
            var plannedPoint = FindPhraseAlignedTransition(track, targetPerTrack, minimum, maximum);
            track.PlannedTransitionSeconds = index == SuggestedOrder.Count - 1 ? null : plannedPoint;
            var fadeTail = index == SuggestedOrder.Count - 1
                ? 0
                : Math.Clamp(track.Analysis!.SuggestedFadeSeconds, .5, 6);
            track.PlannedEndSeconds = Math.Min(track.Analysis!.SourceEndSeconds, plannedPoint + fadeTail);
        }
    }

    private List<LongMixTrack> LimitOrderToTarget(List<LongMixTrack> ordered, out int candidateCount)
    {
        candidateCount = ordered.Count;
        if (ordered.Count <= 2) return ordered;
        var targetSeconds = ParsePlanningValue(TargetMinutesBox?.Text, 30, 1, 360) * 60;
        var minimum = ParsePlanningValue(MinTrackSecondsBox?.Text, 60, 10, 900);
        var maximum = ParsePlanningValue(MaxTrackSecondsBox?.Text, 150, minimum, 1800);
        var ideal = (int)Math.Round(targetSeconds / ((minimum + maximum) / 2), MidpointRounding.AwayFromZero);
        var fewest = Math.Max(2, (int)Math.Ceiling(targetSeconds / maximum));
        var most = Math.Max(2, (int)Math.Floor(targetSeconds / minimum));
        var count = Math.Clamp(ideal, Math.Min(fewest, ordered.Count), Math.Min(most, ordered.Count));
        return ordered.Take(count).ToList();
    }

    private static double FindPhraseAlignedTransition(LongMixTrack track, double targetDuration,
        double minimumDuration, double maximumDuration)
    {
        var analysis = track.Analysis!;
        var duration = TrackDuration(track);
        var earliestOffset = Math.Min(duration, minimumDuration);
        var latestOffset = Math.Min(duration - .2, maximumDuration);
        if (latestOffset <= earliestOffset) return analysis.SourceStartSeconds + Math.Max(0, latestOffset);
        var beatSeconds = analysis.Bpm > 1 ? 60 / analysis.Bpm : .5;
        var phraseSeconds = beatSeconds * 16;
        var anchorOffset = analysis.SuggestedTransitionSeconds - analysis.SourceStartSeconds;
        var phraseIndex = Math.Round((Math.Clamp(targetDuration, earliestOffset, latestOffset) - anchorOffset) / phraseSeconds);
        var candidate = anchorOffset + phraseIndex * phraseSeconds;
        while (candidate < earliestOffset) candidate += phraseSeconds;
        while (candidate > latestOffset) candidate -= phraseSeconds;
        if (candidate < earliestOffset || candidate > latestOffset)
            candidate = Math.Clamp(targetDuration, earliestOffset, latestOffset);
        return Math.Round(analysis.SourceStartSeconds + candidate, 3);
    }

    private void RefreshSuggestionSummary()
    {
        if (SuggestedDurationText is null) return;
        if (SuggestedOrder.Count == 0)
        {
            SuggestedDurationText.Text = "–";
            SuggestedCountText.Text = "Ingen spelordning skapad";
            RemoveWeakestButton.IsEnabled = false;
            return;
        }
        var duration = EstimateMixDuration(SuggestedOrder);
        SuggestedDurationText.Text = FormatDuration(duration);
        var selectedCandidates = Tracks.Count(track => track.IsSelected && track.Analysis is not null);
        SuggestedCountText.Text = selectedCandidates > SuggestedOrder.Count
            ? $"{SuggestedOrder.Count} av {selectedCandidates} kandidater · anpassat till önskad längd"
            : $"{SuggestedOrder.Count} låtar · ungefärlig längd efter övergångar";
        RemoveWeakestButton.IsEnabled = SuggestedOrder.Count > 2;
    }

    private static double EstimateMixDuration(IReadOnlyList<LongMixTrack> order)
    {
        if (order.Count == 0) return 0;
        double timelineStart = 0;
        double outputEnd = PlannedDuration(order[0]);
        for (var index = 0; index < order.Count - 1; index++)
        {
            var current = order[index];
            var transitionOffset = current.Analysis is { } analysis
                ? Math.Clamp((current.PlannedTransitionSeconds ?? analysis.SuggestedTransitionSeconds) - analysis.SourceStartSeconds, 0, TrackDuration(current))
                : Math.Max(0, TrackDuration(current) - 2.5);
            timelineStart += transitionOffset;
            outputEnd = Math.Max(outputEnd, timelineStart + PlannedDuration(order[index + 1]));
        }
        return outputEnd;
    }

    private static double PlannedDuration(LongMixTrack track) => track.Analysis is { } analysis
        ? Math.Clamp((track.PlannedEndSeconds ?? analysis.SourceEndSeconds) - analysis.SourceStartSeconds, .001, TrackDuration(track))
        : TrackDuration(track);

    private static double TrackDuration(LongMixTrack track)
    {
        if (track.Analysis is { } analysis) return Math.Max(.001, analysis.SourceEndSeconds - analysis.SourceStartSeconds);
        var end = track.Jingle.EndSeconds ?? track.Jingle.DurationSeconds;
        return Math.Max(.001, end - track.Jingle.StartSeconds);
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static List<LongMixTrack> BuildBestOrder(IReadOnlyList<LongMixTrack> tracks)
    {
        List<LongMixTrack>? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var start in tracks)
        {
            var remaining = tracks.Where(track => !ReferenceEquals(track, start)).ToList();
            var route = new List<LongMixTrack> { start };
            double total = 0;
            while (remaining.Count > 0)
            {
                var previous = route[^1];
                var next = remaining
                    .Select(track =>
                    {
                        var match = MusicAnalysisService.Compare(previous.Analysis!, track.Analysis!);
                        var energyDrop = Math.Max(0, previous.Analysis!.Energy - track.Analysis!.Energy);
                        return (Track: track, Score: match.Score - energyDrop * .08);
                    })
                    .OrderByDescending(item => item.Score)
                    .First();
                total += next.Score;
                route.Add(next.Track);
                remaining.Remove(next.Track);
            }
            if (total <= bestScore) continue;
            bestScore = total;
            best = route;
        }
        return best ?? tracks.ToList();
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestedOrder.Count < 2) return;
        var transitions = SuggestedOrder
            .Where(track => track.PlannedTransitionSeconds.HasValue)
            .ToDictionary(track => track.Jingle.Id, track => track.PlannedTransitionSeconds!.Value);
        var plannedEnds = SuggestedOrder
            .Where(track => track.PlannedEndSeconds.HasValue)
            .ToDictionary(track => track.Jingle.Id, track => track.PlannedEndSeconds!.Value);
        var editor = new MergeJinglesWindow(_viewModel, SuggestedOrder.Select(track => track.Jingle), true, transitions, plannedEnds)
            { Owner = Owner ?? Application.Current.MainWindow };
        Hide();
        var result = editor.ShowDialog();
        CloseSafely(result == true);
    }

    private void PlanningBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        RebuildSuggestionForPlanningValues();
        e.Handled = true;
    }

    private void PlanningBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        RebuildSuggestionForPlanningValues();

    private void RebuildSuggestionForPlanningValues()
    {
        var candidates = Tracks.Where(track => track.IsSelected && track.Analysis is not null).ToList();
        if (candidates.Count < 2)
        {
            RefreshSuggestedOrderMetadata();
            return;
        }
        var ordered = LimitOrderToTarget(BuildBestOrder(candidates), out _);
        SuggestedOrder.Clear();
        foreach (var track in ordered) SuggestedOrder.Add(track);
        RefreshSuggestedOrderMetadata();
    }

    private static double ParsePlanningValue(string? text, double fallback, double minimum, double maximum)
    {
        var normalized = (text ?? "").Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private void OpenJingleBuilder_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MergeJinglesWindow(_viewModel) { Owner = Owner ?? Application.Current.MainWindow };
        Hide();
        var result = editor.ShowDialog();
        CloseSafely(result == true);
    }

    private void CloseSafely(bool success)
    {
        if (success)
        {
            try
            {
                DialogResult = true;
                return;
            }
            catch (InvalidOperationException)
            {
                // Non-modal internal previews are closed without DialogResult.
            }
        }
        Close();
    }

    private void UpdateSelectionStatus()
    {
        var count = Tracks.Count(track => track.IsSelected);
        StatusText.Text = count == 0 ? "Markera minst två låtar för att börja." : $"{count} låtar markerade.";
    }

    private void UpdateSelectCurrentLabel()
    {
        if (SelectCurrentButton is null) return;
        if (!string.IsNullOrWhiteSpace(SearchBox?.Text))
        {
            SelectCurrentButton.Content = "Markera alla sökträffar";
            return;
        }
        var tab = DeckTabsControl?.SelectedItem as DeckTab;
        SelectCurrentButton.Content = tab?.Deck is null ? "Markera alla låtar" : $"Markera hela {tab.Name}";
    }

    private static double GetDuration(string path)
    {
        using var reader = new AudioFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }
}
