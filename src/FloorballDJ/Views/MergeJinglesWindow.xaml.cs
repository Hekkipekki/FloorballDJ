using System.Globalization;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Views;

public partial class MergeJinglesWindow : Window
{
    private sealed record MergeChoice(Deck? Deck, Jingle Jingle, string Display);
    private readonly MainViewModel _viewModel;
    private readonly JingleMergeService _merge = new();
    private readonly ObservableCollection<MergeChoice> _choices;
    private readonly DispatcherTimer _previewTimer;
    private readonly Stopwatch _previewClock = new();
    private AudioFileReader? _previewReader;
    private WaveOutEvent? _previewOutput;
    private double _previewStart;
    private double _previewEnd;
    private bool _previewingFirst;
    private double _firstTotal;
    private double _secondTotal;
    private double _firstStart;
    private double _firstCrossfade;
    private double _secondStart;
    private double _secondEnd;
    private double _firstCursor;
    private double _secondCursor;

    public MergeJinglesWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _choices = new ObservableCollection<MergeChoice>(viewModel.Decks.SelectMany(deck => deck.Jingles
            .Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath))
            .Select(jingle => new MergeChoice(deck, jingle, $"{deck.Name}  ·  {jingle.Title}"))));
        FirstCombo.ItemsSource = _choices;
        SecondCombo.ItemsSource = _choices;
        FirstCombo.SelectedIndex = _choices.Count > 0 ? 0 : -1;
        SecondCombo.SelectedIndex = _choices.Count > 1 ? 1 : FirstCombo.SelectedIndex;
        TransitionModeCombo.SelectedIndex = 0;
        TitleBox.Text = "Ny kombinerad jingle";
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _previewTimer.Tick += PreviewTimer_Tick;
        Closed += (_, _) => DisposePreview();
        UpdateSecondEndState();
    }

    private async void FirstCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FirstCombo.SelectedItem is not MergeChoice choice) return;
        DisposePreview();
        _firstTotal = ReadDuration(choice.Jingle.FilePath);
        var effectiveEnd = EffectiveEnd(choice.Jingle, _firstTotal);
        _firstStart = Math.Clamp(choice.Jingle.StartSeconds, 0, effectiveEnd);
        _firstCrossfade = Math.Max(_firstStart, effectiveEnd - ReadTransitionOrDefault());
        _firstCursor = _firstCrossfade;
        FirstWaveform.FilePath = choice.Jingle.FilePath;
        await FirstWaveform.LoadAsync(choice.Jingle.FilePath);
        if (ReferenceEquals(FirstCombo.SelectedItem, choice)) UpdateFirstUi();
    }

    private async void SecondCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SecondCombo.SelectedItem is not MergeChoice choice) return;
        DisposePreview();
        _secondTotal = ReadDuration(choice.Jingle.FilePath);
        _secondStart = Math.Clamp(choice.Jingle.StartSeconds, 0, _secondTotal);
        _secondEnd = EffectiveEnd(choice.Jingle, _secondTotal);
        _secondCursor = _secondStart;
        UseSecondEndCheck.IsChecked = choice.Jingle.EndSeconds.HasValue;
        SecondWaveform.FilePath = choice.Jingle.FilePath;
        await SecondWaveform.LoadAsync(choice.Jingle.FilePath);
        if (ReferenceEquals(SecondCombo.SelectedItem, choice)) UpdateSecondUi();
    }

    private void OpenFirstFile_Click(object sender, RoutedEventArgs e) => OpenExternalFile(true);
    private void OpenSecondFile_Click(object sender, RoutedEventArgs e) => OpenExternalFile(false);

    private void OpenExternalFile(bool first)
    {
        var dialog = new OpenFileDialog
        {
            Title = first ? "Välj första ljudfil" : "Välj andra ljudfil",
            Filter = "Ljudfiler|*.mp3;*.wav;*.aiff;*.wma;*.m4a;*.aac;*.flac;*.mp4|Alla filer|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var reader = new AudioFileReader(dialog.FileName);
            var jingle = new Jingle
            {
                Title = Path.GetFileNameWithoutExtension(dialog.FileName),
                FilePath = dialog.FileName,
                DurationSeconds = reader.TotalTime.TotalSeconds,
                StartSeconds = 0,
                EndSeconds = null
            };
            var choice = new MergeChoice(null, jingle, $"Extern fil  ·  {jingle.Title}");
            _choices.Add(choice);
            if (first) FirstCombo.SelectedItem = choice;
            else SecondCombo.SelectedItem = choice;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ljudfilen kunde inte öppnas", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TransitionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionLengthLabel is null) return;
        TransitionLengthLabel.Text = IsOverlayMode ? "Fade in-längd" : "Crossfade-längd";
        FirstTransitionLabel.Text = IsOverlayMode ? "Andra startar" : "Crossfade från";
        SetTransitionStartButton.Content = IsOverlayMode ? "Sätt startpunkt" : "Sätt crossfade";
        TransitionBox.ToolTip = IsOverlayMode
            ? "Det första ljudet fortsätter oförändrat till sitt slut. Det andra startar vid markeringen och tonas in under denna tid. Sätt 0 för direkt start."
            : "Båda ljuden tonas samtidigt under denna tid. Övergången startar vid markeringen i första jinglen.";
    }

    private void FirstWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DisposePreview();
        _firstCursor = PositionToSeconds(e, FirstWaveformSurface, _firstTotal);
        FirstWaveform.PositionFraction = Fraction(_firstCursor, _firstTotal);
        FirstCursorText.Text = $"Markör {FormatTime(_firstCursor)}";
    }

    private void SecondWaveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DisposePreview();
        _secondCursor = PositionToSeconds(e, SecondWaveformSurface, _secondTotal);
        SecondWaveform.PositionFraction = Fraction(_secondCursor, _secondTotal);
        SecondCursorText.Text = $"Markör {FormatTime(_secondCursor)}";
    }

    private void SetFirstStart_Click(object sender, RoutedEventArgs e)
    {
        _firstStart = Math.Min(_firstCursor, _firstCrossfade);
        UpdateFirstUi();
    }

    private void SetCrossfadeStart_Click(object sender, RoutedEventArgs e)
    {
        _firstCrossfade = Math.Max(_firstCursor, _firstStart);
        UpdateFirstUi();
    }

    private void SetSecondStart_Click(object sender, RoutedEventArgs e)
    {
        _secondStart = Math.Min(_secondCursor, _secondEnd);
        UpdateSecondUi();
    }

    private void SetSecondEnd_Click(object sender, RoutedEventArgs e)
    {
        UseSecondEndCheck.IsChecked = true;
        _secondEnd = Math.Max(_secondCursor, _secondStart);
        UpdateSecondUi();
    }

    private void UseSecondEnd_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSecondEndState();
        UpdateSecondUi();
    }

    private void PlayFirst_Click(object sender, RoutedEventArgs e) => StartPreview(true);
    private void PlaySecond_Click(object sender, RoutedEventArgs e) => StartPreview(false);

    private void StartPreview(bool first)
    {
        var choice = first ? FirstCombo.SelectedItem as MergeChoice : SecondCombo.SelectedItem as MergeChoice;
        if (choice is null || !File.Exists(choice.Jingle.FilePath)) return;

        var total = first ? _firstTotal : _secondTotal;
        var startBoundary = first ? _firstStart : _secondStart;
        var endBoundary = first
            ? EffectiveEnd(choice.Jingle, total)
            : UseSecondEndCheck.IsChecked == true ? _secondEnd : EffectiveEnd(choice.Jingle, total);
        var cursor = first ? _firstCursor : _secondCursor;
        if (cursor < startBoundary || cursor >= endBoundary) cursor = startBoundary;

        DisposePreview();
        _previewingFirst = first;
        _previewStart = cursor;
        _previewEnd = endBoundary;
        _previewReader = new AudioFileReader(choice.Jingle.FilePath) { CurrentTime = TimeSpan.FromSeconds(cursor) };
        ISampleProvider source = _previewReader;
        if (Math.Abs(choice.Jingle.PitchSemitones) >= .01)
            source = new SmbPitchShiftingSampleProvider(source)
            {
                PitchFactor = (float)Math.Pow(2, choice.Jingle.PitchSemitones / 12)
            };
        var gainDb = choice.Jingle.GainDb
            + (choice.Jingle.NormalizationEnabled ? choice.Jingle.NormalizationGainDb : 0)
            + _viewModel.Settings.MasterVolumeDb;
        var volume = new VolumeSampleProvider(source)
        {
            Volume = (float)Math.Clamp(Math.Pow(10, gainDb / 20), 0, 4)
        };
        var effects = new DjEffectsSampleProvider(volume, choice.Jingle,
            _viewModel.Settings.MasterLimiterCeilingDbtp, _viewModel.Settings.MasterLimiterEnabled);
        _previewOutput = new WaveOutEvent();
        _previewOutput.Init(effects.ToWaveProvider());
        _previewOutput.Play();
        _previewClock.Restart();
        _previewTimer.Start();
        UpdatePreviewUi(cursor);
    }

    private void PausePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOutput?.PlaybackState == PlaybackState.Playing)
        {
            var position = Math.Min(_previewEnd, _previewStart + _previewClock.Elapsed.TotalSeconds);
            _previewStart = position;
            _previewClock.Reset();
            _previewOutput.Pause();
            _previewTimer.Stop();
            UpdatePreviewUi(position);
        }
        else if (_previewOutput?.PlaybackState == PlaybackState.Paused)
        {
            _previewOutput.Play();
            _previewClock.Restart();
            _previewTimer.Start();
        }
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e) => DisposePreview();

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_previewOutput is null) return;
        var position = Math.Min(_previewEnd, _previewStart + _previewClock.Elapsed.TotalSeconds);
        UpdatePreviewUi(position);
        if (position >= _previewEnd || _previewOutput.PlaybackState == PlaybackState.Stopped)
            DisposePreview();
    }

    private void UpdatePreviewUi(double position)
    {
        if (_previewingFirst)
        {
            _firstCursor = position;
            FirstWaveform.PositionFraction = Fraction(position, _firstTotal);
            FirstCursorText.Text = $"Markör {FormatTime(position)}";
            FirstPreviewText.Text = $"{FormatTime(position)} / {FormatTime(_previewEnd)}";
        }
        else
        {
            _secondCursor = position;
            SecondWaveform.PositionFraction = Fraction(position, _secondTotal);
            SecondCursorText.Text = $"Markör {FormatTime(position)}";
            SecondPreviewText.Text = $"{FormatTime(position)} / {FormatTime(_previewEnd)}";
        }
    }

    private void DisposePreview()
    {
        _previewTimer?.Stop();
        _previewClock.Reset();
        try { _previewOutput?.Stop(); } catch { }
        _previewOutput?.Dispose();
        _previewReader?.Dispose();
        _previewOutput = null;
        _previewReader = null;
        if (FirstPreviewText is not null) FirstPreviewText.Text = "";
        if (SecondPreviewText is not null) SecondPreviewText.Text = "";
    }

    private void UpdateSecondEndState()
    {
        if (SecondEndBox is null || SetSecondEndButton is null) return;
        var enabled = UseSecondEndCheck.IsChecked == true;
        SecondEndBox.IsEnabled = enabled;
        SetSecondEndButton.IsEnabled = enabled;
    }

    private void TimingBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitTimingBox(box);
        e.Handled = true;
    }

    private void TimingBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitTimingBox(box);
    }

    private void CommitTimingBox(TextBox box)
    {
        if (!TryParseTime(box.Text, out var value))
        {
            UpdateFirstUi();
            UpdateSecondUi();
            return;
        }

        if (ReferenceEquals(box, FirstStartBox)) _firstStart = Math.Clamp(value, 0, _firstCrossfade);
        else if (ReferenceEquals(box, FirstCrossfadeBox)) _firstCrossfade = Math.Clamp(value, _firstStart, _firstTotal);
        else if (ReferenceEquals(box, SecondStartBox)) _secondStart = Math.Clamp(value, 0, _secondEnd);
        else if (ReferenceEquals(box, SecondEndBox)) _secondEnd = Math.Clamp(value, _secondStart, _secondTotal);
        UpdateFirstUi();
        UpdateSecondUi();
    }

    private void UpdateFirstUi()
    {
        if (FirstStartBox is null) return;
        _firstStart = Math.Clamp(_firstStart, 0, _firstTotal);
        _firstCrossfade = Math.Clamp(_firstCrossfade, _firstStart, _firstTotal);
        FirstStartBox.Text = FormatTime(_firstStart);
        FirstCrossfadeBox.Text = FormatTime(_firstCrossfade);
        FirstCursorText.Text = $"Markör {FormatTime(_firstCursor)}";
        FirstWaveform.StartFraction = Fraction(_firstStart, _firstTotal);
        FirstWaveform.EndFraction = Fraction(_firstCrossfade, _firstTotal);
        FirstWaveform.PositionFraction = Fraction(_firstCursor, _firstTotal);
    }

    private void UpdateSecondUi()
    {
        if (SecondStartBox is null) return;
        _secondStart = Math.Clamp(_secondStart, 0, _secondTotal);
        _secondEnd = Math.Clamp(_secondEnd, _secondStart, _secondTotal);
        SecondStartBox.Text = FormatTime(_secondStart);
        SecondEndBox.Text = FormatTime(_secondEnd);
        SecondCursorText.Text = $"Markör {FormatTime(_secondCursor)}";
        SecondWaveform.StartFraction = Fraction(_secondStart, _secondTotal);
        SecondWaveform.EndFraction = UseSecondEndCheck.IsChecked == true ? Fraction(_secondEnd, _secondTotal) : 1;
        SecondWaveform.PositionFraction = Fraction(_secondCursor, _secondTotal);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Spara kombinerad jingle",
            Filter = "Okomprimerat WAV-ljud|*.wav",
            DefaultExt = ".wav",
            AddExtension = true,
            FileName = SafeFileName(TitleBox.Text) + ".wav"
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CommitTimingBox(FirstStartBox);
        CommitTimingBox(FirstCrossfadeBox);
        CommitTimingBox(SecondStartBox);
        if (UseSecondEndCheck.IsChecked == true) CommitTimingBox(SecondEndBox);

        if (FirstCombo.SelectedItem is not MergeChoice first || SecondCombo.SelectedItem is not MergeChoice second)
        {
            MessageBox.Show(this, "Välj två jinglar.");
            return;
        }
        if (first.Jingle.Id == second.Jingle.Id)
        {
            MessageBox.Show(this, "Välj två olika jinglar.");
            return;
        }
        if (string.IsNullOrWhiteSpace(PathBox.Text))
        {
            Browse_Click(sender, e);
            if (string.IsNullOrWhiteSpace(PathBox.Text)) return;
        }
        if (!TryParseNumber(TransitionBox.Text, out var transition) || transition < 0 || transition > 30)
        {
            MessageBox.Show(this, "Ange en crossfade mellan 0 och 30 sekunder.");
            return;
        }
        if (!IsOverlayMode && _firstCrossfade + transition > EffectiveEnd(first.Jingle, _firstTotal) + 0.0001)
        {
            MessageBox.Show(this, "Crossfaden ryms inte efter den markerade punkten i första jinglen. Flytta markeringen tidigare eller korta övergången.");
            return;
        }

        CreateButton.IsEnabled = false;
        StatusText.Text = "Skapar förlustfri WAV-fil…";
        try
        {
            await _merge.MergeAsync(first.Jingle, second.Jingle, _firstStart, _firstCrossfade,
                _secondStart, UseSecondEndCheck.IsChecked == true ? _secondEnd : null,
                transition, IsOverlayMode, PathBox.Text);
            using var reader = new AudioFileReader(PathBox.Text);
            var targetDeck = _viewModel.SelectedDeck ?? _viewModel.Decks.First();
            var target = targetDeck.Jingles.FirstOrDefault(jingle => !jingle.HasAudio);
            if (target is null)
            {
                targetDeck.Rows++;
                _viewModel.ApplyLayout();
                target = targetDeck.Jingles.First(jingle => !jingle.HasAudio);
            }
            target.Title = string.IsNullOrWhiteSpace(TitleBox.Text)
                ? Path.GetFileNameWithoutExtension(PathBox.Text)
                : TitleBox.Text.Trim();
            target.FilePath = PathBox.Text;
            target.StartSeconds = 0;
            target.EndSeconds = null;
            target.DurationSeconds = reader.TotalTime.TotalSeconds;
            _viewModel.Status = $"Skapade och lade till {target.Title}";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Kunde inte skapa filen.";
            MessageBox.Show(this, ex.Message, "Kombinering misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
            CreateButton.IsEnabled = true;
        }
    }

    private double ReadTransitionOrDefault() => TryParseNumber(TransitionBox?.Text, out var value) ? Math.Max(0, value) : 2;
    private bool IsOverlayMode => (TransitionModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string == "Overlay";
    private static double ReadDuration(string path)
    {
        using var reader = new AudioFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }
    private static double EffectiveEnd(Jingle jingle, double total) => Math.Clamp(jingle.EndSeconds ?? total, 0, total);
    private static double Fraction(double seconds, double total) => total <= 0 ? 0 : Math.Clamp(seconds / total, 0, 1);
    private static double PositionToSeconds(MouseButtonEventArgs e, FrameworkElement surface, double total) =>
        Math.Clamp(e.GetPosition(surface).X / Math.Max(1, surface.ActualWidth), 0, 1) * total;
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff");

    private static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace(',', '.');
        if (!text.Contains(':')) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return false;
        var values = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        seconds = values.Length == 2 ? values[0] * 60 + values[1] : values[0] * 3600 + values[1] * 60 + values[2];
        return seconds >= 0;
    }

    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string SafeFileName(string value) => string.Concat(
        (value.Trim().Length == 0 ? "Kombinerad jingle" : value.Trim())
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
