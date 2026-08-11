using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;
using NAudio.Wave;

namespace FloorballDJ.Views;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac", ".mp4", ".ogg" };
    private readonly ProjectService _projects = new();
    private readonly ProfilePreferencesService _profilePreferences;
    private readonly AudioEngine _audio = new();
    private readonly LicenseService _licenses;
    private const string JingleClipboardFormat = "FloorballDJ.Jingle.Json.v1";
    private Jingle? _clipboard;
    private Point _dragStart;
    private Jingle? _dragSource;
    private Button? _activeDropTarget;
    private bool _suppressNextClick;
    private Jingle? _spaceResumeJingle;
    private TimeSpan _spaceResumePosition;
    private DateTimeOffset _spaceResumeExpires;
    private bool _closeInProgress;
    private bool _closeCommitted;
    private Point _deckDragStart;
    private Deck? _deckDragSource;
    private bool _deckDragActive;
    private TabItem? _deckDropTarget;
    private int _deckDropInsertionIndex = -1;
    private bool _deckDropAfterTarget;
    private readonly System.Windows.Threading.DispatcherTimer _inlineSearchClearTimer = new()
        { Interval = TimeSpan.FromSeconds(4) };
    private readonly System.Windows.Threading.DispatcherTimer _inlineSearchDebounceTimer = new()
        { Interval = TimeSpan.FromMilliseconds(220) };
    private Jingle? _inlineSearchHighlight;
    private string _lastInlineSearchQuery = "";
    private int _inlineSearchIndex = -1;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow() : this(new LicenseService())
    {
    }

    public MainWindow(LicenseService licenses)
    {
        _licenses = licenses;
        _profilePreferences = new ProfilePreferencesService(_projects.AppDataDirectory, _projects.DefaultProjectPath);
        InitializeComponent();
        _inlineSearchClearTimer.Tick += (_, _) =>
        {
            _inlineSearchClearTimer.Stop();
            ClearInlineSearchHighlight();
        };
        _inlineSearchDebounceTimer.Tick += (_, _) =>
        {
            _inlineSearchDebounceTimer.Stop();
            FindNextInlineSearchResult(false);
        };
        RefreshLicenseStatus();
        SourceInitialized += MainWindow_SourceInitialized;
        DataContext = new MainViewModel(_projects, _profilePreferences, _audio);
        ViewModel.PrimaryPlaybackStarted += (_, _) =>
        {
            if (TalkToggle.IsChecked == true) TalkToggle.IsChecked = false;
        };
        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            RefreshOutputName();
            if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                RefreshRecentProfilesMenu();
                WindowState = WindowState.Maximized;
                await Task.Delay(150);
                WindowState = WindowState.Normal;
                var settings = new SettingsWindow(ViewModel.Project, _audio.GetOutputDevices(), _profilePreferences) { Owner = this };
                settings.Show();
                await Task.Delay(250);
                settings.Close();
                var appearance = new ButtonAppearanceWindow("#142238", "#F7FAFC", false,
                    ViewModel.Settings.FontFamily, ViewModel.Settings.TitleFontSize) { Owner = this };
                appearance.Show();
                await Task.Delay(250);
                appearance.Close();
                var prompt = new TextPromptWindow("Test", "Test", "Test", "Deck 1") { Owner = this };
                prompt.Show();
                await Task.Delay(150);
                prompt.Close();
                var deckLayout = new DeckLayoutWindow(ViewModel.Decks.First()) { Owner = this };
                deckLayout.Show();
                await Task.Delay(150);
                deckLayout.Close();
                var deckFades = new DeckFadeWindow(ViewModel.Decks.First()) { Owner = this };
                deckFades.Show();
                await Task.Delay(150);
                deckFades.Close();
                var revisions = new RevisionHistoryWindow(ViewModel) { Owner = this };
                revisions.Show();
                await Task.Delay(250);
                revisions.Close();
                var properties = new JinglePropertiesWindow(new Jingle(), _audio.GetOutputDevices(), ViewModel.Settings.MasterVolumeDb) { Owner = this };
                properties.Show();
                await Task.Delay(250);
                properties.Close();
                var loudness = new LoudnessBatchWindow(ViewModel) { Owner = this };
                loudness.Show();
                await Task.Delay(250);
                loudness.Close();
                var audioFiles = new ManageAudioFilesWindow(ViewModel, _projects) { Owner = this };
                audioFiles.Show();
                await Task.Delay(250);
                audioFiles.Close();
                EmbeddedAutoplay.Visibility = Visibility.Visible;
                await EmbeddedAutoplay.RefreshAvailableAsync();
                await Task.Delay(250);
                EmbeddedAutoplay.Visibility = Visibility.Collapsed;
                var help = new HelpWindow { Owner = this };
                help.Show();
                if (help.FindName("HelpNavigation") is ListBox helpNavigation)
                    for (var page = 0; page < helpNavigation.Items.Count; page++)
                    {
                        helpNavigation.SelectedIndex = page;
                        await Task.Delay(40);
                    }
                help.Close();
                var search = new JingleSearchWindow(ViewModel.Decks) { Owner = this };
                search.Show();
                await Task.Delay(180);
                search.Close();
                var merge = new MergeJinglesWindow(ViewModel) { Owner = this };
                merge.Show();
                await Task.Delay(180);
                merge.Close();
                Close();
            }
        };
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCommitted)
        {
            ViewModel.Dispose();
            return;
        }

        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
            await ViewModel.SaveAsync();
        }
        catch (Exception ex)
        {
            var closeAnyway = MessageBox.Show(this,
                $"De senaste ändringarna kunde inte sparas.\n\n{ex.Message}\n\nVill du stänga ändå?",
                "Kunde inte spara", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!closeAnyway)
            {
                _closeInProgress = false;
                return;
            }
        }
        ViewModel.Dispose();
        _closeCommitted = true;
        Application.Current.Shutdown();
    }

    private async void Jingle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressNextClick) { _suppressNextClick = false; return; }
        if ((sender as Button)?.DataContext is not Jingle jingle) return;
        if (jingle.IsTextBlock) return;
        if (!jingle.HasAudio) { await LoadAudioAsync(jingle); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ViewModel.ToggleQueue(jingle);
            e.Handled = true;
            return;
        }
        ClearSpaceResume();
        try { ViewModel.Play(jingle); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kunde inte spela", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Jingle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragSource = (sender as FrameworkElement)?.DataContext as Jingle;
        _suppressNextClick = false;
    }

    private void Jingle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null || sender is not Button button) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _suppressNextClick = true;
        var source = _dragSource;
        _dragSource = null;
        var originalOpacity = button.Opacity;
        var originalTransform = button.RenderTransform;
        var originalEffect = button.Effect;
        button.Opacity = 0.35;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = new ScaleTransform(0.92, 0.92);
        button.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Color.FromRgb(54, 224, 180), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.75 };
        try { DragDrop.DoDragDrop(button, new DataObject(typeof(Jingle), source), DragDropEffects.Move); }
        finally
        {
            ClearActiveDropTarget();
            button.Opacity = originalOpacity;
            button.RenderTransform = originalTransform;
            button.Effect = originalEffect;
        }
    }

    private void Jingle_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as Jingle;
        var droppedFiles = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (target is not null && droppedFiles is not null && droppedFiles.Any(IsAudioFile))
            e.Effects = DragDropEffects.Copy;
        else
        {
            var source = e.Data.GetData(typeof(Jingle)) as Jingle;
            e.Effects = source is not null && target is not null && source != target &&
                        ViewModel.Decks.Any(deck => deck.Jingles.Contains(source) && deck.Jingles.Contains(target))
                ? DragDropEffects.Move : DragDropEffects.None;
        }
        if (sender is Button button)
        {
            if (_activeDropTarget is not null && !ReferenceEquals(_activeDropTarget, button))
                ClearDropTarget(_activeDropTarget);
            _activeDropTarget = button;
            button.BorderBrush = e.Effects is DragDropEffects.Move or DragDropEffects.Copy
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("DangerBrush");
            button.BorderThickness = new Thickness(2);
            SetDropCue(button, e.Effects is DragDropEffects.Move or DragDropEffects.Copy,
                e.Effects == DragDropEffects.Copy
                    ? $"SLÄPP {droppedFiles!.Count(IsAudioFile)} LJUD"
                    : "BYT PLATS");
        }
        e.Handled = true;
    }

    private void Jingle_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;
        var point = e.GetPosition(button);
        if (point.X >= 0 && point.Y >= 0 && point.X <= button.ActualWidth && point.Y <= button.ActualHeight) return;
        ClearDropTarget(button);
    }

    private async void Jingle_Drop(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as Jingle;
        if (sender is Button button)
            ClearDropTarget(button);
        if (target is not null && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var added = await AssignAudioFilesAsync(target, files);
            if (added > 0)
            {
                _suppressNextClick = true;
            }
            e.Handled = true;
            return;
        }

        var source = e.Data.GetData(typeof(Jingle)) as Jingle;
        if (source is null || target is null || source == target) return;
        var deck = ViewModel.Decks.FirstOrDefault(candidate => candidate.Jingles.Contains(source) && candidate.Jingles.Contains(target));
        if (deck is null) return;

        var sourceIndex = deck.Jingles.IndexOf(source);
        var targetIndex = deck.Jingles.IndexOf(target);
        // Swap the two fixed slots explicitly. Keeping Position and collection index in
        // lockstep prevents an empty target from being rendered one cell beside the drop.
        target.Position = sourceIndex;
        source.Position = targetIndex;
        deck.Jingles[sourceIndex] = target;
        deck.Jingles[targetIndex] = source;
        for (var index = 0; index < deck.Jingles.Count; index++)
            deck.Jingles[index].Position = index;
        DeckTabsControl.Items.Refresh();
        ViewModel.Status = $"Bytte plats på {source.Title} och {target.Title}";
        ViewModel.NotifyJingleChanged();
        _suppressNextClick = true;
        e.Handled = true;
        await SaveSafelyAsync();
    }

    private static void SetDropCue(Button button, bool visible, string text)
    {
        var cue = FindVisualChild<Border>(button, "DropCue");
        if (cue is null) return;
        cue.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (FindVisualChild<TextBlock>(cue, "DropCueText") is { } label) label.Text = text;
    }

    private void ClearDropTarget(Button button)
    {
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.BorderThicknessProperty);
        SetDropCue(button, false, "");
        if (ReferenceEquals(_activeDropTarget, button)) _activeDropTarget = null;
    }

    private void ClearActiveDropTarget()
    {
        if (_activeDropTarget is { } button) ClearDropTarget(button);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && match.Name == name) return match;
            if (FindVisualChild<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _audio.StopAll();
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSpaceResumePending && _spaceResumeJingle is not null && DateTimeOffset.Now <= _spaceResumeExpires)
        {
            var resume = _spaceResumeJingle;
            var position = _spaceResumePosition;
            ClearSpaceResume();
            if (ViewModel.AutoplayModeActive && ViewModel.PlaybackQueue.Contains(resume)) ViewModel.PlayQueuedItem(resume);
            else ViewModel.Play(resume);
            _audio.Seek(position);
            _audio.PublishSnapshot();
            return;
        }
        await _audio.PauseOrResumeAsync();
    }
    private async void Fade_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.MarkFadingOut();
        await _audio.FadeOutAllAsync(ViewModel.Settings.FadeOutSeconds);
    }
    private async void SessionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Settings.TrackSession = SessionToggle.IsChecked == true;
        if (!vm.Settings.TrackSession)
            foreach (var jingle in vm.Decks.SelectMany(deck => deck.Jingles)) jingle.SessionPlayCount = 0;
        try { await vm.SaveAsync(); } catch { }
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.Settings.MasterVolumeDb = e.NewValue;
        viewModel.ConfigureAudio();
    }

    private void MasterVolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MasterVolumeSlider.Value = Math.Clamp(MasterVolumeSlider.Value + (e.Delta > 0 ? .5 : -.5),
            MasterVolumeSlider.Minimum, MasterVolumeSlider.Maximum);
        e.Handled = true;
    }

    private void MasterVolumeText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dialog = new TextPromptWindow(
            "Ljudnivå",
            "Ljudnivå (dB)",
            "Ange ett värde mellan -60 och +6 dB.",
            MasterVolumeSlider.Value.ToString("0.#", CultureInfo.CurrentCulture)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!double.TryParse(dialog.Value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            MessageBox.Show(this, "Ange ett giltigt tal, till exempel 0, -0,5 eller -12.", "Ogiltig ljudnivå", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MasterVolumeSlider.Value = Math.Clamp(value, MasterVolumeSlider.Minimum, MasterVolumeSlider.Maximum);
    }

    private void SecondaryOutputToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var enabled = SecondaryOutputToggle.IsChecked == true;
        if (enabled && string.IsNullOrWhiteSpace(viewModel.Settings.SecondaryOutputDeviceId))
        {
            MessageBox.Show(this, "Välj först Utgångsenhet 2 under Verktyg > Inställningar.", "Ingen förlyssningsutgång vald", MessageBoxButton.OK, MessageBoxImage.Information);
            SecondaryOutputToggle.IsChecked = false;
            return;
        }
        if (!enabled && viewModel.UseSecondaryOutput) _audio.StopSecondaryOutput();
        viewModel.SetSecondaryOutput(enabled);
        RefreshOutputName();
    }

    private async void TalkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var active = TalkToggle.IsChecked == true;
        TalkToggle.ToolTip = active
            ? "PA/Talk aktiv – klicka för att tona upp musiken igen"
            : "PA/Talk: tona ned musiken medan speakern talar";
        await _audio.SetTalkDuckingAsync(active,
            active ? viewModel.Settings.FadeOutSeconds : viewModel.Settings.FadeInSeconds);
        viewModel.Status = active ? "PA/Talk aktiv – musiken är nedtonad" : "PA/Talk av – normal ljudnivå";
    }

    private void MergeJingles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MergeJinglesWindow(ViewModel) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ViewModel.NotifyJingleChanged();
        ViewModel.RequestSave();
    }

    private void JingleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (JingleSearchPlaceholder is null) return;
        var hasText = !string.IsNullOrWhiteSpace(JingleSearchBox.Text);
        JingleSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        _lastInlineSearchQuery = "";
        _inlineSearchIndex = -1;
        _inlineSearchDebounceTimer.Stop();
        ClearInlineSearchHighlight();
        if (hasText) _inlineSearchDebounceTimer.Start();
    }

    private void JingleSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _inlineSearchDebounceTimer.Stop();
            PlayInlineSearchResult();
        }
        else if (e.Key is Key.Down or Key.Tab)
        {
            e.Handled = true;
            _inlineSearchDebounceTimer.Stop();
            FindNextInlineSearchResult(true);
            JingleSearchBox.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _inlineSearchDebounceTimer.Stop();
            JingleSearchBox.Clear();
            Keyboard.ClearFocus();
        }
    }

    private void PlayInlineSearchResult()
    {
        _inlineSearchDebounceTimer.Stop();
        var match = _inlineSearchHighlight ?? FindNextInlineSearchResult(false);
        if (match is null) return;
        ClearSpaceResume();
        try
        {
            ViewModel.Play(match);
            ViewModel.Status = $"Spelar {match.Title}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kunde inte spela", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Jingle? FindNextInlineSearchResult(bool advance)
    {
        var query = JingleSearchBox.Text.Trim();
        if (query.Length == 0) return null;

        var matches = ViewModel.Decks
            .SelectMany(deck => deck.Jingles.Where(jingle => jingle.HasAudio)
                .Select(jingle => new { Deck = deck, Jingle = jingle, Score = InlineMatchScore(jingle.Title, query) }))
            .Where(result => result.Score < int.MaxValue)
            .OrderBy(result => result.Score)
            .ThenBy(result => result.Jingle.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (matches.Count == 0)
        {
            ClearInlineSearchHighlight();
            ViewModel.Status = $"Ingen jingle eller låt matchade ”{query}”";
            return null;
        }

        if (!string.Equals(_lastInlineSearchQuery, query, StringComparison.CurrentCultureIgnoreCase))
        {
            _lastInlineSearchQuery = query;
            _inlineSearchIndex = 0;
        }
        else if (advance)
        {
            _inlineSearchIndex = (_inlineSearchIndex + 1) % matches.Count;
        }

        var match = matches[Math.Clamp(_inlineSearchIndex, 0, matches.Count - 1)];
        ClearInlineSearchHighlight();
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        ViewModel.SetAutoplayMode(false);
        ViewModel.SelectedDeck = match.Deck;
        DeckTabsControl.SelectedItem = match.Deck;
        match.Jingle.IsSearchMatch = true;
        _inlineSearchHighlight = match.Jingle;
        // Behåll markeringen så länge söktexten finns kvar. Det gör att Enter alltid
        // spelar den träff användaren faktiskt ser och att Tab kan bläddra stabilt.
        _inlineSearchClearTimer.Stop();
        ViewModel.Status = $"Hittade {match.Jingle.Title} i {match.Deck.Name} · {(_inlineSearchIndex + 1)} av {matches.Count}";
        return match.Jingle;
    }

    private void ClearInlineSearchHighlight()
    {
        if (_inlineSearchHighlight is null) return;
        _inlineSearchHighlight.IsSearchMatch = false;
        _inlineSearchHighlight = null;
    }

    private static int InlineMatchScore(string title, string query)
    {
        var normalizedTitle = title.ToLowerInvariant();
        var normalizedQuery = query.ToLowerInvariant();
        if (normalizedTitle == normalizedQuery) return 0;
        if (normalizedTitle.StartsWith(normalizedQuery)) return 1;
        var contains = normalizedTitle.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (contains >= 0) return 10 + contains;
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words.All(normalizedTitle.Contains)) return 50;
        var distance = InlineLevenshtein(normalizedTitle, normalizedQuery);
        return distance <= Math.Max(3, normalizedQuery.Length / 3) ? 100 + distance : int.MaxValue;
    }

    private static int InlineLevenshtein(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1),
                    previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return row[b.Length];
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Den automatiska sökningen kan byta aktivt deck. WPF kan då flytta
        // tangentfokus från sökrutan trots att texten och markeringen är kvar.
        // Låt därför Enter spela den synliga träffen även efter ett sådant
        // fokusbyte.
        if (e.Key == Key.Enter &&
            !string.IsNullOrWhiteSpace(JingleSearchBox.Text) &&
            _inlineSearchHighlight is not null)
        {
            e.Handled = true;
            PlayInlineSearchResult();
            return;
        }

        if (JingleSearchBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                PlayInlineSearchResult();
                return;
            }
            if (e.Key is Key.Down or Key.Tab)
            {
                e.Handled = true;
                _inlineSearchDebounceTimer.Stop();
                FindNextInlineSearchResult(true);
                JingleSearchBox.Focus();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _inlineSearchDebounceTimer.Stop();
                JingleSearchBox.Clear();
                Keyboard.ClearFocus();
                return;
            }
        }

        if (!e.IsRepeat && e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            new HelpWindow { Owner = this }.ShowDialog();
            return;
        }

        if (!e.IsRepeat && e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control &&
            Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            e.Handled = true;
            await SaveSafelyAsync();
            return;
        }

        if (!e.IsRepeat && e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            JingleSearchBox.Focus();
            JingleSearchBox.SelectAll();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down &&
            Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            e.Handled = true;
            MasterVolumeSlider.Value = Math.Clamp(MasterVolumeSlider.Value + (e.Key == Key.Up ? .5 : -.5),
                MasterVolumeSlider.Minimum, MasterVolumeSlider.Maximum);
            return;
        }

        if (!e.IsRepeat && Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            var categoryAnchor = ViewModel.Decks.SelectMany(deck => deck.Jingles)
                .FirstOrDefault(jingle => jingle.HasAudio && !string.IsNullOrWhiteSpace(jingle.Category) &&
                    ShortcutService.Matches(jingle.CategoryShortcut, e));
            if (categoryAnchor is not null)
            {
                var candidates = ViewModel.Decks.SelectMany(deck => deck.Jingles)
                    .Where(jingle => jingle.HasAudio && string.Equals(jingle.Category.Trim(), categoryAnchor.Category.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    .ToArray();
                if (candidates.Length > 0)
                {
                    e.Handled = true;
                    ClearSpaceResume();
                    var selected = candidates[Random.Shared.Next(candidates.Length)];
                    try { ViewModel.Play(selected); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kunde inte spela", MessageBoxButton.OK, MessageBoxImage.Warning); }
                    return;
                }
            }
            var selectedDeckMatches = ViewModel.SelectedDeck?.Jingles
                .Where(jingle => jingle.HasAudio && ShortcutService.Matches(jingle.Shortcut, e)) ?? [];
            var shortcutJingle = selectedDeckMatches.FirstOrDefault() ?? ViewModel.Decks
                .Where(deck => deck != ViewModel.SelectedDeck)
                .SelectMany(deck => deck.Jingles)
                .FirstOrDefault(jingle => jingle.HasAudio && ShortcutService.Matches(jingle.Shortcut, e));
            if (shortcutJingle is not null)
            {
                e.Handled = true;
                ClearSpaceResume();
                if (shortcutJingle.ShortcutSwitchesDeck)
                {
                    var shortcutDeck = ViewModel.Decks.FirstOrDefault(deck => deck.Jingles.Contains(shortcutJingle));
                    if (shortcutDeck is not null)
                    {
                        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
                        ViewModel.SetAutoplayMode(false);
                        ViewModel.SelectedDeck = shortcutDeck;
                        DeckTabsControl.SelectedItem = shortcutDeck;
                    }
                }
                try { ViewModel.Play(shortcutJingle); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kunde inte spela", MessageBoxButton.OK, MessageBoxImage.Warning); }
                return;
            }
        }

        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (ViewModel.UseSecondaryOutput)
        {
            await _audio.FadeOutPrimaryOutputAsync(ViewModel.Settings.FadeOutSeconds);
            return;
        }
        var autoplayActive = ViewModel.AutoplayModeActive;
        if (!autoplayActive && ViewModel.HasDeckPlaybackQueue)
        {
            ClearSpaceResume();
            if (ViewModel.NowPlaying.JingleId is not null)
            {
                ViewModel.MarkFadingOut();
                await _audio.FadeOutAllAsync(ViewModel.Settings.FadeOutSeconds);
                return;
            }
            ViewModel.PlayNextDeckQueued();
            return;
        }
        if (ViewModel.NowPlaying.JingleId is not null)
        {
            _spaceResumeJingle = autoplayActive
                ? ViewModel.ActiveQueueItem
                : ViewModel.Decks.SelectMany(deck => deck.Jingles).FirstOrDefault(jingle => jingle.Id == ViewModel.NowPlaying.JingleId);
            var livePosition = _audio.GetCurrentPosition() ?? ViewModel.NowPlaying.Position;
            var fadeEnd = livePosition + TimeSpan.FromSeconds(ViewModel.Settings.FadeOutSeconds);
            _spaceResumePosition = fadeEnd > ViewModel.NowPlaying.Duration ? ViewModel.NowPlaying.Duration : fadeEnd;
            _spaceResumeExpires = DateTimeOffset.Now.AddMinutes(2);
            ViewModel.IsSpaceResumePending = _spaceResumeJingle is not null;
            ViewModel.MarkFadingOut();
            await _audio.FadeOutAllAsync(ViewModel.Settings.FadeOutSeconds);
        }
        else if (_spaceResumeJingle is not null && DateTimeOffset.Now <= _spaceResumeExpires)
        {
            var resume = _spaceResumeJingle;
            var position = _spaceResumePosition;
            ClearSpaceResume();
            if (autoplayActive && ViewModel.PlaybackQueue.Contains(resume)) ViewModel.PlayQueuedItem(resume);
            else ViewModel.Play(resume);
            _audio.Seek(position);
            _audio.PublishSnapshot();
        }
        else if (autoplayActive && ViewModel.PlaybackQueue.Count > 0)
        {
            ClearSpaceResume();
            ViewModel.PlayNextQueued();
        }
        else ClearSpaceResume();
    }

    private void ClearSpaceResume()
    {
        _spaceResumeJingle = null;
        _spaceResumePosition = TimeSpan.Zero;
        _spaceResumeExpires = default;
        ViewModel.IsSpaceResumePending = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Menu) return;
            if (current == sender) break;
        }
        if (e.ClickCount == 2) ToggleMaximized();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WindowMessageHook);
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (message != wmGetMinMaxInfo) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var work = monitorInfo.WorkArea;
        var area = monitorInfo.MonitorArea;
        info.MaxPosition.X = Math.Abs(work.Left - area.Left);
        info.MaxPosition.Y = Math.Abs(work.Top - area.Top);
        info.MaxSize.X = Math.Abs(work.Right - work.Left);
        info.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private void DeckTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ViewModel.SetAutoplayMode(false);
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        if (DeckTabsControl.Items.Count < 2) return;
        var current = Math.Max(0, DeckTabsControl.SelectedIndex);
        DeckTabsControl.SelectedIndex = e.Delta < 0
            ? (current + 1) % DeckTabsControl.Items.Count
            : (current - 1 + DeckTabsControl.Items.Count) % DeckTabsControl.Items.Count;
        e.Handled = true;
    }

    private void DeckTabs_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab?.Content is not Deck deck) return;
        tab.IsSelected = true;

        var menu = new ContextMenu();
        menu.Items.Add(CreateDeckMenuItem("Byt namn…", deck, RenameDeck_Click));
        menu.Items.Add(CreateDeckMenuItem("Lägg till nytt deck efter detta", deck, AddDeck_Click));
        menu.Items.Add(CreateDeckMenuItem("Ta bort deck", deck, RemoveDeck_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateDeckMenuItem("Ändra rader och kolumner…", deck, ChangeDeckLayout_Click));
        menu.Items.Add(CreateDeckMenuItem("Ställ in fade in/ut…", deck, ChangeDeckFades_Click));
        tab.ContextMenu = menu;
        menu.PlacementTarget = tab;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem CreateDeckMenuItem(string header, Deck deck, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header, Tag = deck };
        item.Click += handler;
        return item;
    }

    private async void RenameDeck_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDeck(sender) is not { } deck) return;
        var dialog = new TextPromptWindow("Byt namn på deck", "Namn på deck", "Namnet visas i fliken.", deck.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        deck.Name = dialog.Value;
        ViewModel.Status = $"Bytte namn till {deck.Name}";
        await SaveSafelyAsync();
    }

    private async void AddDeck_Click(object sender, RoutedEventArgs e)
    {
        var reference = GetContextDeck(sender) ?? ViewModel.SelectedDeck;
        var index = reference is null ? ViewModel.Decks.Count : ViewModel.Decks.IndexOf(reference) + 1;
        var deck = new Deck
        {
            Name = $"Deck {ViewModel.Decks.Count + 1}",
            Rows = reference?.Rows ?? ViewModel.Settings.Rows,
            Columns = reference?.Columns ?? ViewModel.Settings.Columns
        };
        ViewModel.Decks.Insert(Math.Clamp(index, 0, ViewModel.Decks.Count), deck);
        ViewModel.Settings.DeckCount = ViewModel.Decks.Count;
        ViewModel.ApplyLayout();
        ViewModel.SelectedDeck = deck;
        DeckTabsControl.Items.Refresh();
        ViewModel.Status = $"Lade till {deck.Name}";
        await SaveSafelyAsync();
    }

    private async void RemoveDeck_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDeck(sender) is not { } deck) return;
        if (ViewModel.Decks.Count <= 1)
        {
            MessageBox.Show(this, "Profilen måste innehålla minst ett deck.", "Kan inte ta bort", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var audioCount = deck.Jingles.Count(item => item.HasAudio);
        var detail = audioCount > 0 ? $" Decket innehåller {audioCount} ljudfiler som tas bort från profilen." : "";
        if (MessageBox.Show(this, $"Ta bort '{deck.Name}'?{detail}\n\nLjudfilerna på disken raderas inte.", "Ta bort deck",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var oldIndex = ViewModel.Decks.IndexOf(deck);
        ViewModel.Decks.Remove(deck);
        ViewModel.Settings.DeckCount = ViewModel.Decks.Count;
        ViewModel.SelectedDeck = ViewModel.Decks[Math.Clamp(oldIndex, 0, ViewModel.Decks.Count - 1)];
        ViewModel.ApplyLayout();
        DeckTabsControl.Items.Refresh();
        ViewModel.Status = $"Tog bort {deck.Name}";
        await SaveSafelyAsync();
    }

    private async void ChangeDeckLayout_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDeck(sender) is not { } deck) return;
        var dialog = new DeckLayoutWindow(deck) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var hiddenAudio = ProjectService.CountHiddenAudioAfterResize(deck, dialog.Rows, dialog.Columns);
        if (hiddenAudio > 0 && MessageBox.Show(this,
                $"Den nya layouten har inte plats för {hiddenAudio} jinglar. De finns kvar dolda och visas igen om decket förstoras. Fortsätta?",
                "Dolda jinglar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ProjectService.ResizeDeckLayout(deck, dialog.Rows, dialog.Columns);
        ViewModel.ApplyLayout();
        ViewModel.Status = $"{deck.Name}: {deck.Rows} rader × {deck.Columns} kolumner";
        await SaveSafelyAsync();
    }

    private async void ChangeDeckFades_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDeck(sender) is not { } deck) return;
        var dialog = new DeckFadeWindow(deck) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var changed = 0;
        foreach (var jingle in deck.Jingles.Where(item => item.HasAudio))
        {
            jingle.FadeInOverrideSeconds = dialog.FadeInSeconds;
            jingle.FadeOutOverrideSeconds = dialog.FadeOutSeconds;
            changed++;
        }
        ViewModel.NotifyJingleChanged();
        ViewModel.Status = $"{deck.Name}: uppdaterade fade in/ut för {changed} jinglar";
        await SaveSafelyAsync();
    }

    private static Deck? GetContextDeck(object sender)
    {
        if (sender is not MenuItem item) return null;
        if (item.Tag is Deck taggedDeck) return taggedDeck;
        var contextMenu = item.Parent as ContextMenu;
        while (contextMenu is null && item.Parent is MenuItem parent)
        {
            item = parent;
            contextMenu = item.Parent as ContextMenu;
        }
        return (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as Deck;
    }

    private void DeckTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        EndDeckDrag();
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        _deckDragStart = e.GetPosition(DeckTabsControl);
        _deckDragSource = tab?.DataContext as Deck ?? tab?.Content as Deck;
        if (tab is not null)
        {
            EmbeddedAutoplay.Visibility = Visibility.Collapsed;
            ViewModel.SetAutoplayMode(false);
        }
    }

    private void DeckTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // MouseUp completes the operation. Clearing here can discard the insertion
            // point on systems that report a final move after the button was released.
            if (!_deckDragActive) _deckDragSource = null;
            return;
        }
        if (_deckDragSource is null) return;
        var current = e.GetPosition(DeckTabsControl);
        if (!_deckDragActive)
        {
            if (Math.Abs(current.X - _deckDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _deckDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _deckDragActive = true;
            Mouse.Capture(DeckTabsControl, CaptureMode.SubTree);
            DeckTabsControl.Cursor = Cursors.SizeWE;
        }

        UpdateDeckDropPosition(current);
        e.Handled = true;
    }

    private void DeckTabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = _deckDragSource;
        var insertionIndex = _deckDropInsertionIndex;
        var moved = _deckDragActive && source is not null && insertionIndex >= 0;
        EndDeckDrag();
        if (!moved || source is null) return;
        ViewModel.MoveDeck(source, insertionIndex);
        DeckTabsControl.Items.Refresh();
        e.Handled = true;
    }

    private void UpdateDeckDropPosition(Point pointer)
    {
        var (insertionIndex, tab, afterTarget) = GetDeckDropPosition(pointer);
        var sourceIndex = _deckDragSource is null ? -1 : ViewModel.Decks.IndexOf(_deckDragSource);
        var finalIndex = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
        if (sourceIndex < 0 || insertionIndex < 0 || finalIndex == sourceIndex)
        {
            ClearDeckDropTarget();
            return;
        }

        if (ReferenceEquals(tab, _deckDropTarget) && insertionIndex == _deckDropInsertionIndex && afterTarget == _deckDropAfterTarget) return;
        ClearDeckDropTarget();
        _deckDropTarget = tab;
        _deckDropInsertionIndex = insertionIndex;
        _deckDropAfterTarget = afterTarget;
        if (_deckDropTarget is not null)
        {
            _deckDropTarget.BorderBrush = (Brush)FindResource("AccentBrush");
            _deckDropTarget.BorderThickness = afterTarget
                ? new Thickness(0, 0, 3, 0)
                : new Thickness(3, 0, 0, 0);
        }
    }

    private (int InsertionIndex, TabItem? Target, bool AfterTarget) GetDeckDropPosition(Point pointer)
    {
        TabItem? lastTab = null;
        for (var index = 0; index < DeckTabsControl.Items.Count; index++)
        {
            if (DeckTabsControl.ItemContainerGenerator.ContainerFromIndex(index) is not TabItem candidate) continue;
            lastTab = candidate;
            var origin = candidate.TransformToAncestor(DeckTabsControl).Transform(new Point(0, 0));
            if (pointer.X < origin.X + candidate.ActualWidth / 2)
                return (index, candidate, false);
        }

        return lastTab is null
            ? (-1, null, false)
            : (DeckTabsControl.Items.Count, lastTab, true);
    }

    private void EndDeckDrag()
    {
        _deckDragActive = false;
        _deckDragSource = null;
        ClearDeckDropTarget();
        DeckTabsControl.ClearValue(CursorProperty);
        if (ReferenceEquals(Mouse.Captured, DeckTabsControl)) Mouse.Capture(null);
    }

    private void ClearDeckDropTarget()
    {
        if (_deckDropTarget is not null)
        {
            _deckDropTarget.ClearValue(Border.BorderBrushProperty);
            _deckDropTarget.ClearValue(Border.BorderThicknessProperty);
        }
        _deckDropTarget = null;
        _deckDropInsertionIndex = -1;
        _deckDropAfterTarget = false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void Autoplay_Click(object sender, RoutedEventArgs e)
    {
        DeckTabsControl.SelectedIndex = -1;
        EmbeddedAutoplay.Visibility = Visibility.Visible;
        ViewModel.SetAutoplayMode(true);
        await EmbeddedAutoplay.RefreshAvailableAsync();
    }

    private async void LoadAudio_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is { } jingle) await LoadAudioAsync(jingle);
    }

    private async Task LoadAudioAsync(Jingle jingle)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Välj en eller flera ljudfiler",
            Filter = "Ljudfiler|*.mp3;*.wav;*.aiff;*.aif;*.wma;*.m4a;*.aac;*.flac;*.mp4;*.ogg|Alla filer|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (jingle.IsTextBlock) jingle.IsTextBlock = false;
        await AssignAudioFilesAsync(jingle, dialog.FileNames);
    }

    private async Task<int> AssignAudioFilesAsync(Jingle startJingle, IEnumerable<string> paths)
    {
        var files = paths.Where(IsAudioFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return 0;
        var deck = ViewModel.Decks.FirstOrDefault(candidate => candidate.Jingles.Contains(startJingle));
        if (deck is null) return 0;

        var cursor = Math.Max(0, deck.Jingles.IndexOf(startJingle));
        var added = 0;
        var addedRows = 0;
        foreach (var path in files)
        {
            var slotIndex = FindAvailableSlot(deck, cursor);
            while (slotIndex < 0 && deck.Rows < ProjectService.MaximumDeckRows)
            {
                deck.Rows++;
                addedRows++;
                ViewModel.ApplyLayout();
                slotIndex = FindAvailableSlot(deck, cursor);
            }
            if (slotIndex < 0) break;

            var jingle = deck.Jingles[slotIndex];
            jingle.IsTextBlock = false;
            jingle.FilePath = path;
            jingle.Title = Path.GetFileNameWithoutExtension(path);
            try { using var reader = new AudioFileReader(path); jingle.DurationSeconds = reader.TotalTime.TotalSeconds; }
            catch { jingle.DurationSeconds = 0; }
            added++;
            cursor = slotIndex + 1;
        }

        ViewModel.NotifyJingleChanged();
        ViewModel.Status = addedRows > 0
            ? $"Lade till {added} ljudfiler och utökade {deck.Name} med {addedRows} rader"
            : $"Lade till {added} ljudfiler i {deck.Name}";
        await SaveSafelyAsync();
        if (added < files.Length)
            MessageBox.Show(this, $"{files.Length - added} ljudfiler kunde inte läggas till eftersom decket nådde säkerhetsgränsen på {ProjectService.MaximumDeckRows} rader.",
                "Decket är fullt", MessageBoxButton.OK, MessageBoxImage.Warning);
        return added;
    }

    private static int FindAvailableSlot(Deck deck, int startIndex)
    {
        var capacity = Math.Min(deck.Jingles.Count, deck.Rows * Math.Max(1, deck.Columns));
        if (capacity <= 0) return -1;
        var start = Math.Clamp(startIndex, 0, capacity);
        for (var index = start; index < capacity; index++)
            if (!deck.Jingles[index].HasContent) return index;
        for (var index = 0; index < start; index++)
            if (!deck.Jingles[index].HasContent) return index;
        return -1;
    }

    private static bool IsAudioFile(string path) => File.Exists(path) && AudioExtensions.Contains(Path.GetExtension(path));

    private async void TextBlock_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle) return;
        var convertingAudio = jingle.HasAudio;
        var wasEmpty = !jingle.HasContent;
        var isNew = !jingle.IsTextBlock;
        var dialog = new TextPromptWindow(
            convertingAudio ? "Gör om till textblock" : isNew ? "Lägg till textblock" : "Redigera textblock",
            "Text på deckrutan",
            "Textblocket används som en visuell rubrik eller avdelare och kan inte spelas eller köas.",
            jingle.Title) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (convertingAudio && MessageBox.Show(this,
                "Ljudkopplingen tas bort från den här rutan. Originalfilen på disken påverkas inte. Fortsätta?",
                "Gör om till textblock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        jingle.IsTextBlock = true;
        jingle.FilePath = "";
        jingle.Title = dialog.Value;
        jingle.DurationSeconds = 0;
        jingle.StartSeconds = 0;
        jingle.EndSeconds = null;
        jingle.QueuePosition = 0;
        jingle.AutoplayQueuePosition = 0;
        if (wasEmpty)
        {
            jingle.ButtonColor = "#101010";
            jingle.TextColor = "#FFFFFF";
        }
        ViewModel.NotifyJingleChanged(jingle);
        ViewModel.Status = convertingAudio ? $"Gjorde om {jingle.Title} till textblock"
            : isNew ? $"Lade till textblocket {jingle.Title}" : $"Uppdaterade textblocket {jingle.Title}";
        await SaveSafelyAsync();
    }

    private void JingleProperties_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle) return;
        var dialog = new JinglePropertiesWindow(jingle, _audio.GetOutputDevices(), ViewModel.Settings.MasterVolumeDb) { Owner = this };
        if (dialog.ShowDialog() == true) { ViewModel.NotifyJingleChanged(jingle); ViewModel.RequestSave(); }
    }

    private void ButtonAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle) return;
        EditAppearance(jingle, false);
    }

    private void RowAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle) return;
        EditAppearance(jingle, true);
    }

    private void EditAppearance(Jingle jingle, bool entireRow)
    {
        var deck = ViewModel.Decks.FirstOrDefault(candidate => candidate.Jingles.Contains(jingle));
        if (deck is null) return;
        var dialog = new ButtonAppearanceWindow(jingle.ButtonColor, jingle.TextColor, entireRow,
            ViewModel.Settings.FontFamily, ViewModel.Settings.TitleFontSize) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (entireRow)
        {
            var row = jingle.Position / Math.Max(1, deck.Columns);
            var first = row * deck.Columns;
            var last = Math.Min(deck.Jingles.Count, first + deck.Columns);
            for (var index = first; index < last; index++)
            {
                var item = deck.Jingles[index];
                item.ButtonColor = dialog.ButtonColor;
                item.TextColor = dialog.TextColor;
                deck.Jingles[index] = item;
            }
        }
        else
        {
            jingle.ButtonColor = dialog.ButtonColor;
            jingle.TextColor = dialog.TextColor;
            ViewModel.NotifyJingleChanged(jingle);
        }
        ViewModel.Status = entireRow ? $"Uppdaterade rad {jingle.Position / Math.Max(1, deck.Columns) + 1}" : $"Uppdaterade {jingle.Title}";
        ViewModel.RequestSave();
    }

    private void CopyJingle_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle) return;
        _clipboard = Clone(jingle);
        try
        {
            var data = new DataObject();
            data.SetData(JingleClipboardFormat, JsonSerializer.Serialize(_clipboard));
            Clipboard.SetDataObject(data, true);
            ViewModel.Status = $"Kopierade {jingle.Title} – kan klistras in i ett annat FloorballDJ-fönster";
        }
        catch
        {
            ViewModel.Status = $"Kopierade {jingle.Title} i detta fönster";
        }
    }

    private void PasteJingle_Click(object sender, RoutedEventArgs e)
    {
        var source = ReadJingleClipboard() ?? _clipboard;
        if (source is null || GetContextJingle(sender) is not { } target) return;
        CopyInto(source, target, keepPosition: true);
        ViewModel.NotifyJingleChanged(target);
        ViewModel.Status = $"Klistrade in {source.Title} på vald plats";
        ViewModel.RequestSave();
    }

    private static Jingle? ReadJingleClipboard()
    {
        try
        {
            if (!Clipboard.ContainsData(JingleClipboardFormat)) return null;
            var json = Clipboard.GetData(JingleClipboardFormat) as string;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 128_000) return null;
            return JsonSerializer.Deserialize<Jingle>(json);
        }
        catch { return null; }
    }

    private void PlayMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode } item && GetContextJingle(item) is { } jingle && Enum.TryParse<JinglePlayMode>(mode, out var parsed))
        {
            jingle.PlayMode = parsed;
            if (item.Parent is MenuItem parent)
                foreach (var sibling in parent.Items.OfType<MenuItem>().Where(candidate => !string.Equals(candidate.Tag?.ToString(), "MultipleClicks", StringComparison.OrdinalIgnoreCase)))
                    sibling.IsChecked = ReferenceEquals(sibling, item);
            ViewModel.NotifyJingleChanged(jingle); ViewModel.Status = $"{jingle.Title}: {parsed}"; ViewModel.RequestSave();
        }
    }

    private void PlayModeMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent || GetContextJingle(parent) is not { } jingle) return;
        foreach (var item in parent.Items.OfType<MenuItem>())
            item.IsChecked = string.Equals(item.Tag?.ToString(), "MultipleClicks", StringComparison.OrdinalIgnoreCase)
                ? jingle.AllowMultipleClicks
                : string.Equals(item.Tag?.ToString(), jingle.PlayMode.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void MultipleClicks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || GetContextJingle(item) is not { } jingle) return;
        jingle.AllowMultipleClicks = item.IsChecked;
        ViewModel.NotifyJingleChanged(jingle);
        ViewModel.Status = item.IsChecked
            ? $"{jingle.Title}: flera samtidiga klick tillåts"
            : $"{jingle.Title}: ett klick i taget";
        ViewModel.RequestSave();
    }

    private void ClearJingle_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextJingle(sender) is not { } jingle || !jingle.HasContent) return;
        var detail = jingle.HasAudio ? " Ljudfilen på disken raderas inte." : "";
        if (MessageBox.Show(this, $"Töm rutan '{jingle.Title}'?{detail}", "Töm ruta", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var position = jingle.Position;
        CopyInto(new Jingle { Position = position }, jingle, true);
        ViewModel.NotifyJingleChanged(jingle);
    }

    private async Task<bool> SaveSafelyAsync(string? path = null)
    {
        try
        {
            await ViewModel.SaveAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Kunde inte spara: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Kunde inte spara", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveSafelyAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "FloorballDJ-projekt|*.floorballdj.json", FileName = $"{ViewModel.Project.Name}.floorballdj.json" };
        if (dialog.ShowDialog(this) == true) await SaveSafelyAsync(dialog.FileName);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "FloorballDJ-projekt|*.floorballdj.json|Alla filer|*.*" };
        if (dialog.ShowDialog(this) == true) await OpenProfileAsync(dialog.FileName);
    }

    private void ProfileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        RefreshRecentProfilesMenu();
    }

    private void RefreshRecentProfilesMenu()
    {
        RecentProfilesMenu.Items.Clear();
        var defaultPath = _profilePreferences.GetDefaultProfilePath();
        var recentProfiles = _profilePreferences.GetRecentProfiles();
        if (recentProfiles.Count == 0)
        {
            RecentProfilesMenu.Items.Add(new MenuItem { Header = "Inga profiler ännu", IsEnabled = false });
            return;
        }

        for (var index = 0; index < recentProfiles.Count; index++)
        {
            var path = recentProfiles[index];
            var isDefault = string.Equals(path, defaultPath, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = $"{index + 1}. {(isDefault ? "★ " : "")}{GetProfileDisplayName(path)}",
                ToolTip = path,
                Tag = path,
                FontWeight = isDefault ? FontWeights.SemiBold : FontWeights.Normal
            };
            item.Click += RecentProfile_Click;
            RecentProfilesMenu.Items.Add(item);
        }

        RecentProfilesMenu.Items.Add(new Separator());
        var clearItem = new MenuItem { Header = "Rensa historiken" };
        clearItem.Click += (_, _) =>
        {
            _profilePreferences.ClearRecentProfiles();
            RefreshRecentProfilesMenu();
        };
        RecentProfilesMenu.Items.Add(clearItem);
    }

    private async void RecentProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string path) return;
        await OpenProfileAsync(path);
    }

    private async Task OpenProfileAsync(string path)
    {
        if (!File.Exists(path))
        {
            _profilePreferences.RemoveRecentProfile(path);
            RefreshRecentProfilesMenu();
            MessageBox.Show(this, $"Profilen finns inte längre:\n{path}", "Profilen saknas",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!await SaveSafelyAsync()) return;
        try
        {
            _audio.StopAll();
            ViewModel.ReplaceQueue([]);
            ViewModel.SetAutoplayMode(false);
            EmbeddedAutoplay.Visibility = Visibility.Collapsed;
            ClearSpaceResume();
            await ViewModel.LoadAsync(path);
            RefreshRecentProfilesMenu();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kunde inte öppna profilen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetProfileDisplayName(string path)
    {
        var fileName = Path.GetFileName(path);
        const string profileSuffix = ".floorballdj.json";
        return fileName.EndsWith(profileSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^profileSuffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Skapa ett nytt tomt projekt? Det nuvarande projektet autosparas först.", "Nytt projekt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!await SaveSafelyAsync()) return;
        _audio.StopAll();
        ViewModel.ReplaceQueue([]);
        ViewModel.SetAutoplayMode(false);
        ViewModel.SetSecondaryOutput(false);
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        SecondaryOutputToggle.IsChecked = false;
        ClearSpaceResume();
        ViewModel.ImportLegacyXml(CreateTemporaryEmptyXml());
    }

    private static string CreateTemporaryEmptyXml()
    {
        var path = Path.Combine(Path.GetTempPath(), "floorballdj-new.xml");
        File.WriteAllText(path, "<JinglePlaylist><Rows>4</Rows><Cols>5</Cols><Tabs>4</Tabs></JinglePlaylist>");
        return path;
    }

    private void ImportXml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Snap Jingle Player XML|*.xml|Alla filer|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ViewModel.ImportLegacyXml(dialog.FileName);
            var slots = ViewModel.Decks.Sum(deck => deck.Rows * deck.Columns);
            var jingles = ViewModel.Decks.Sum(deck => deck.Jingles.Count(jingle => jingle.HasAudio));
            MessageBox.Show(this,
                $"Profilen importerades med {ViewModel.Decks.Count} deck, {slots} knappplatser och {jingles} jinglar.\n\n" +
                "Om ljudfilerna har flyttats kan du koppla om dem via Verktyg > Kontrollera ljudfiler.",
                "Snap-profil importerad", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Importen misslyckades", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var path = await ViewModel.BackupAsync();
        MessageBox.Show(this, $"Backup skapad:\n{path}", "Backup klar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var beforeDecks = ViewModel.Settings.DeckCount;
        var beforeRows = ViewModel.Settings.Rows;
        var beforeColumns = ViewModel.Settings.Columns;
        var beforeLayouts = ViewModel.Decks.ToDictionary(deck => deck.Id, deck => (deck.Rows, deck.Columns));
        var dialog = new SettingsWindow(ViewModel.Project, _audio.GetOutputDevices(), _profilePreferences) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var requestedLayouts = ViewModel.Decks.ToDictionary(deck => deck.Id, deck => (deck.Rows, deck.Columns));
        foreach (var deck in ViewModel.Decks)
            if (beforeLayouts.TryGetValue(deck.Id, out var layout)) { deck.Rows = layout.Rows; deck.Columns = layout.Columns; }
        var hidesJingles = ViewModel.Decks.Any(deck => requestedLayouts.TryGetValue(deck.Id, out var layout) &&
                ProjectService.CountHiddenAudioAfterResize(deck, layout.Rows, layout.Columns) > 0) ||
            (ViewModel.Settings.DeckCount < beforeDecks && ViewModel.Decks.Skip(ViewModel.Settings.DeckCount).SelectMany(x => x.Jingles).Any(x => x.HasAudio));
        if (hidesJingles && MessageBox.Show(this, "Den nya layouten döljer jinglar. De sparas fortfarande i projektet och visas igen om layouten förstoras. Fortsätta?", "Dolda jinglar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            ViewModel.Settings.DeckCount = beforeDecks;
            ViewModel.Settings.Rows = beforeRows;
            ViewModel.Settings.Columns = beforeColumns;
            foreach (var deck in ViewModel.Decks)
                if (beforeLayouts.TryGetValue(deck.Id, out var layout)) { deck.Rows = layout.Rows; deck.Columns = layout.Columns; }
            return;
        }
        foreach (var deck in ViewModel.Decks)
            if (requestedLayouts.TryGetValue(deck.Id, out var layout)) ProjectService.ResizeDeckLayout(deck, layout.Rows, layout.Columns);
        ViewModel.ApplyLayout();
        ViewModel.ConfigureAudio();
        RefreshOutputName();
        ViewModel.RequestSave();
    }

    private void PlayerWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement waveform || ViewModel.NowPlaying.Duration <= TimeSpan.Zero) return;
        var fraction = Math.Clamp(e.GetPosition(waveform).X / Math.Max(1, waveform.ActualWidth), 0, 1);
        _audio.Seek(TimeSpan.FromTicks((long)(ViewModel.NowPlaying.Duration.Ticks * fraction)));
        _audio.PublishSnapshot();
    }

    private void FindMissing_Click(object sender, RoutedEventArgs e)
    {
        new ManageAudioFilesWindow(ViewModel, _projects) { Owner = this }.ShowDialog();
    }

    private void Loudness_Click(object sender, RoutedEventArgs e)
    {
        new LoudnessBatchWindow(ViewModel) { Owner = this }.ShowDialog();
        ViewModel.ConfigureAudio();
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "FloorballDJ 0.1\nModern jingle cart för sportevenemang.\n\nFörsta fungerande grundversionen.", "Om FloorballDJ");
    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UpdateWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InstallerPath) || !File.Exists(dialog.InstallerPath)) return;
        if (ViewModel.NowPlaying.JingleId is not null)
        {
            MessageBox.Show(this,
                "Stoppa pågående ljud innan uppdateringen installeras. Den verifierade installationsfilen finns kvar och behöver inte hämtas manuellt.",
                "Ljud spelas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await SaveSafelyAsync()) return;

        try
        {
            Process.Start(new ProcessStartInfo(dialog.InstallerPath)
            {
                UseShellExecute = true,
                Arguments = "/SP- /NORESTART"
            });
            _closeCommitted = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kunde inte starta installationen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void License_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LicenseWindow(_licenses, _licenses.Current, isStartup: false) { Owner = this };
        dialog.ShowDialog();
        RefreshLicenseStatus();
        if (dialog.LicenseWasDeactivated) Close();
    }

    private void RefreshLicenseStatus()
    {
        LicenseStatusText.Text = _licenses.Current.Kind switch
        {
            LicenseAccessKind.Trial when _licenses.Current.ExpiresAt is { } expires =>
                $"PROVPERIOD  {Math.Max(0, (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalDays))} dagar kvar",
            LicenseAccessKind.Licensed => "LICENS AKTIV",
            _ => "LICENS EJ AKTIV"
        };
    }
    private void RevisionHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RevisionHistoryWindow(ViewModel) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _audio.StopAll();
        ClearSpaceResume();
        ViewModel.SetAutoplayMode(false);
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        ViewModel.ApplyLayout();
        RefreshOutputName();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshOutputName()
    {
        var selectedId = ViewModel.UseSecondaryOutput ? ViewModel.Settings.SecondaryOutputDeviceId : ViewModel.Settings.OutputDeviceId;
        var selected = _audio.GetOutputDevices().FirstOrDefault(x => x.Id == selectedId);
        OutputDeviceText.Text = selected?.Name ?? "Windows standardenhet";
        SessionToggle.IsChecked = ViewModel.Settings.TrackSession;
    }

    private static Jingle? GetContextJingle(object sender)
    {
        if (sender is not MenuItem item) return null;
        // The context menu is a separate visual tree. Its explicitly bound DataContext
        // identifies the exact button that was right-clicked, including empty slots.
        if (item.DataContext is Jingle boundJingle) return boundJingle;
        ItemsControl? parent = item.Parent as ItemsControl;
        while (parent is MenuItem menu) parent = menu.Parent as ItemsControl;
        var context = parent as ContextMenu ?? item.Parent as ContextMenu;
        return (context?.PlacementTarget as FrameworkElement)?.DataContext as Jingle;
    }

    private static Jingle Clone(Jingle source) { var copy = new Jingle(); CopyInto(source, copy, false); return copy; }
    private static void CopyInto(Jingle source, Jingle target, bool keepPosition)
    {
        var position = target.Position;
        target.Id = Guid.NewGuid(); target.Title = source.Title; target.FilePath = source.FilePath; target.IsTextBlock = source.IsTextBlock; target.ButtonColor = source.ButtonColor; target.TextColor = source.TextColor;
        target.StartSeconds = source.StartSeconds; target.EndSeconds = source.EndSeconds; target.DurationSeconds = source.DurationSeconds; target.PlayMode = source.PlayMode; target.Loop = source.Loop; target.AllowMultipleClicks = source.AllowMultipleClicks;
        target.GainDb = source.GainDb; target.PitchSemitones = source.PitchSemitones; target.TempoPercent = source.TempoPercent; target.RatePercent = source.RatePercent;
        target.NormalizationEnabled = source.NormalizationEnabled; target.NormalizationTargetLufs = source.NormalizationTargetLufs;
        target.IntegratedLufs = source.IntegratedLufs; target.TruePeakDbtp = source.TruePeakDbtp; target.LoudnessRangeLu = source.LoudnessRangeLu;
        target.MaxMomentaryLufs = source.MaxMomentaryLufs; target.NormalizationGainDb = source.NormalizationGainDb;
        target.AnalysisFileSize = source.AnalysisFileSize; target.AnalysisFileWriteUtcTicks = source.AnalysisFileWriteUtcTicks;
        target.EqLowDb = source.EqLowDb; target.EqMidDb = source.EqMidDb; target.EqHighDb = source.EqHighDb;
        target.CompressorEnabled = source.CompressorEnabled; target.CompressorThresholdDb = source.CompressorThresholdDb; target.CompressorRatio = source.CompressorRatio;
        target.CompressorAttackMs = source.CompressorAttackMs; target.CompressorReleaseMs = source.CompressorReleaseMs;
        target.FadeInOverrideSeconds = source.FadeInOverrideSeconds; target.FadeOutOverrideSeconds = source.FadeOutOverrideSeconds; target.Shortcut = source.Shortcut; target.ShortcutSwitchesDeck = source.ShortcutSwitchesDeck; target.SessionPlayCount = 0;
        target.Position = keepPosition ? position : source.Position;
    }
}
