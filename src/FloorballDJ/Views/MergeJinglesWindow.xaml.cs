using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    public sealed record MergeChoice(Deck? Deck, Jingle Jingle, string Display);

    public sealed class ClipEditor : INotifyPropertyChanged
    {
        private string _tabTitle = "Ljud";
        public required MergeChoice Choice { get; set; }
        public double Total { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public bool UseEnd { get; set; }
        public double Cursor { get; set; }
        public double TransitionStart { get; set; }
        public double FadeOut { get; set; } = 1.5;
        public double FadeIn { get; set; } = .75;
        public double VolumeDb { get; set; }
        public JingleMergeService.TransitionMode TransitionMode { get; set; } = JingleMergeService.TransitionMode.Crossfade;
        public string TabTitle { get => _tabTitle; set { _tabTitle = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly MainViewModel _viewModel;
    private readonly JingleMergeService _merge = new();
    private readonly ObservableCollection<MergeChoice> _choices;
    private readonly DispatcherTimer _previewTimer;
    private readonly Stopwatch _previewClock = new();
    private AudioFileReader? _previewReader;
    private WaveOutEvent? _previewOutput;
    private double _previewStart;
    private double _previewEnd;
    private ClipEditor? _previewClip;
    private string? _previewTempPath;
    private bool _previewingComposition;
    private ClipEditor? _activeClip;
    private bool _loadingUi;
    private bool _draggingWaveform;
    private bool _resumePreviewAfterDrag;
    private double _waveformDragStartX;
    private double _waveformDragStartSeconds;

    public ObservableCollection<ClipEditor> Clips { get; } = [];

    public MergeJinglesWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = this;
        _choices = new ObservableCollection<MergeChoice>(viewModel.Decks.SelectMany(deck => deck.Jingles
            .Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath))
            .Select(jingle => new MergeChoice(deck, jingle, $"{deck.Name}  ·  {jingle.Title}"))));
        SourceCombo.ItemsSource = _choices;

        if (_choices.Count > 0) Clips.Add(CreateClip(_choices[0]));
        if (_choices.Count > 1) Clips.Add(CreateClip(_choices[1]));
        else if (_choices.Count > 0) Clips.Add(CreateClip(_choices[0]));
        RefreshTabTitles();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _previewTimer.Tick += PreviewTimer_Tick;
        Closed += (_, _) => DisposePreview();
        TemplateCombo.SelectedIndex = 0;
        if (Clips.Count > 0) ClipTabs.SelectedIndex = 0;
    }

    private ClipEditor CreateClip(MergeChoice choice)
    {
        var total = ReadDuration(choice.Jingle.FilePath);
        var end = EffectiveEnd(choice.Jingle, total);
        var start = Math.Clamp(choice.Jingle.StartSeconds, 0, end);
        return new ClipEditor
        {
            Choice = choice,
            Total = total,
            Start = start,
            End = end,
            UseEnd = choice.Jingle.EndSeconds.HasValue,
            Cursor = start,
            TransitionStart = Math.Max(start, end - 1.5)
        };
    }

    private async void ClipTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ClipTabs)) return;
        CommitActiveClip();
        DisposePreview();
        _activeClip = ClipTabs.SelectedItem as ClipEditor;
        await LoadActiveClipAsync();
    }

    private async Task LoadActiveClipAsync()
    {
        var clip = _activeClip;
        if (clip is null) return;
        _loadingUi = true;
        SourceCombo.SelectedItem = clip.Choice;
        StartBox.Text = FormatTime(clip.Start);
        EndBox.Text = FormatTime(clip.End);
        UseEndCheck.IsChecked = clip.UseEnd;
        TransitionStartBox.Text = FormatTime(clip.TransitionStart);
        FadeOutBox.Text = FormatNumber(clip.FadeOut);
        FadeInBox.Text = FormatNumber(clip.FadeIn);
        TransitionModeCombo.SelectedIndex = clip.TransitionMode switch
        {
            JingleMergeService.TransitionMode.SequentialFade => 1,
            JingleMergeService.TransitionMode.MixSound => 2,
            _ => 0
        };
        ClipVolumeSlider.Value = clip.VolumeDb;
        ClipVolumeBox.Text = FormatSignedNumber(clip.VolumeDb);
        _loadingUi = false;

        var hasNext = Clips.IndexOf(clip) < Clips.Count - 1;
        TransitionGroup.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
        TransitionMarkerPanel.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
        ClipTimingHeader.Text = $"LJUD {Clips.IndexOf(clip) + 1} · KLIPPGRÄNSER";
        TransitionMarkerLabel.Text = $"START FÖR LJUD {Clips.IndexOf(clip) + 2}";
        UpdateEndState();
        UpdateActiveUi();

        Waveform.FilePath = clip.Choice.Jingle.FilePath;
        await Waveform.LoadAsync(clip.Choice.Jingle.FilePath);
        if (ReferenceEquals(_activeClip, clip)) UpdateActiveUi();
    }

    private async void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || _activeClip is null || SourceCombo.SelectedItem is not MergeChoice choice) return;
        DisposePreview();
        var replacement = CreateClip(choice);
        _activeClip.Choice = choice;
        _activeClip.Total = replacement.Total;
        _activeClip.Start = replacement.Start;
        _activeClip.End = replacement.End;
        _activeClip.UseEnd = replacement.UseEnd;
        _activeClip.Cursor = replacement.Cursor;
        _activeClip.TransitionStart = replacement.TransitionStart;
        RefreshTabTitles();
        await LoadActiveClipAsync();
    }

    private void AddClip_Click(object sender, RoutedEventArgs e)
    {
        if (_choices.Count == 0) { OpenFile_Click(sender, e); return; }
        CommitActiveClip();
        var used = Clips.Select(clip => clip.Choice).ToHashSet();
        var choice = _choices.FirstOrDefault(item => !used.Contains(item)) ?? _choices[0];
        var clip = CreateClip(choice);
        Clips.Add(clip);
        RefreshTabTitles();
        ClipTabs.SelectedItem = clip;
    }

    private void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if (_activeClip is null || Clips.Count <= 2)
        {
            MessageBox.Show(this, "En kombinerad jingle behöver minst två ljud.", "Kan inte ta bort", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var index = Clips.IndexOf(_activeClip);
        Clips.Remove(_activeClip);
        RefreshTabTitles();
        ClipTabs.SelectedIndex = Math.Min(index, Clips.Count - 1);
    }

    private void MoveClipLeft_Click(object sender, RoutedEventArgs e) => MoveActiveClip(-1);
    private void MoveClipRight_Click(object sender, RoutedEventArgs e) => MoveActiveClip(1);

    private void MoveActiveClip(int offset)
    {
        if (_activeClip is null) return;
        CommitActiveClip();
        var oldIndex = Clips.IndexOf(_activeClip);
        var newIndex = Math.Clamp(oldIndex + offset, 0, Clips.Count - 1);
        if (oldIndex == newIndex) return;
        Clips.Move(oldIndex, newIndex);
        RefreshTabTitles();
        ClipTabs.SelectedItem = _activeClip;
        _ = LoadActiveClipAsync();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Välj ljudfil",
            Multiselect = true,
            Filter = "Ljudfiler|*.mp3;*.wav;*.aiff;*.wma;*.m4a;*.aac;*.flac;*.mp4|Alla filer|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                using var reader = new AudioFileReader(path);
                var jingle = new Jingle
                {
                    Title = Path.GetFileNameWithoutExtension(path), FilePath = path,
                    DurationSeconds = reader.TotalTime.TotalSeconds, StartSeconds = 0, EndSeconds = null
                };
                var choice = new MergeChoice(null, jingle, $"Rå fil  ·  {jingle.Title}");
                _choices.Add(choice);
                if (_activeClip is null || dialog.FileNames.Length > 1)
                {
                    var clip = CreateClip(choice);
                    Clips.Add(clip);
                    ClipTabs.SelectedItem = clip;
                }
                else SourceCombo.SelectedItem = choice;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ljudfilen kunde inte öppnas", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        while (Clips.Count > 2 && Clips.Take(2).All(clip => clip.Choice == Clips[2].Choice)) Clips.RemoveAt(0);
        RefreshTabTitles();
    }

    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || TemplateCombo.SelectedItem is not ComboBoxItem item || item.Tag as string == "Custom") return;
        foreach (var clip in Clips.Take(Math.Max(0, Clips.Count - 1)))
        {
            switch (item.Tag as string)
            {
                case "Penalty": clip.TransitionMode = JingleMergeService.TransitionMode.SequentialFade; clip.FadeOut = .35; clip.FadeIn = .2; break;
                case "Smooth": clip.TransitionMode = JingleMergeService.TransitionMode.Crossfade; clip.FadeOut = 2.5; clip.FadeIn = 2.5; break;
                default: clip.TransitionMode = JingleMergeService.TransitionMode.Crossfade; clip.FadeOut = 1.5; clip.FadeIn = .75; break;
            }
        }
        UpdateActiveUi();
        StatusText.Text = "Mallen har ställt in övergångarna. Alla värden kan finjusteras.";
    }

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeClip is null) return;
        _draggingWaveform = true;
        _waveformDragStartX = e.GetPosition(WaveformSurface).X;
        _waveformDragStartSeconds = _activeClip.Cursor;
        _resumePreviewAfterDrag = !_previewingComposition && ReferenceEquals(_previewClip, _activeClip) &&
            (_previewOutput?.PlaybackState is PlaybackState.Playing or PlaybackState.Paused);
        DisposePreview();
        WaveformSurface.CaptureMouse();
        UpdateCursorFromPointer(e);
        e.Handled = true;
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingWaveform || e.LeftButton != MouseButtonState.Pressed || _activeClip is null) return;
        UpdateCursorFromPointer(e);
        e.Handled = true;
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingWaveform) return;
        UpdateCursorFromPointer(e);
        _draggingWaveform = false;
        WaveformSurface.ReleaseMouseCapture();
        if (_resumePreviewAfterDrag) StartPreview();
        _resumePreviewAfterDrag = false;
        e.Handled = true;
    }

    private void UpdateCursorFromPointer(MouseEventArgs e)
    {
        if (_activeClip is null) return;
        var pointX = e.GetPosition(WaveformSurface).X;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var deltaFraction = (pointX - _waveformDragStartX) / Math.Max(1, WaveformSurface.ActualWidth);
            _activeClip.Cursor = Math.Clamp(_waveformDragStartSeconds + deltaFraction * _activeClip.Total * .1, 0, _activeClip.Total);
        }
        else
        {
            _activeClip.Cursor = Math.Clamp(pointX / Math.Max(1, WaveformSurface.ActualWidth), 0, 1) * _activeClip.Total;
        }
        UpdateActiveUi();
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_activeClip is null) return;
        _activeClip.Start = Math.Min(_activeClip.Cursor, _activeClip.End);
        _activeClip.TransitionStart = Math.Max(_activeClip.TransitionStart, _activeClip.Start);
        UpdateActiveUi();
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_activeClip is null) return;
        _activeClip.UseEnd = true;
        _activeClip.End = Math.Max(_activeClip.Cursor, _activeClip.Start);
        UseEndCheck.IsChecked = true;
        UpdateActiveUi();
    }

    private void SetTransition_Click(object sender, RoutedEventArgs e)
    {
        if (_activeClip is null) return;
        _activeClip.TransitionStart = Math.Clamp(_activeClip.Cursor, _activeClip.Start, _activeClip.End);
        UpdateActiveUi();
    }

    private void UseEnd_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingUi && _activeClip is not null) _activeClip.UseEnd = UseEndCheck.IsChecked == true;
        UpdateEndState();
        UpdateActiveUi();
    }

    private void UpdateEndState()
    {
        if (EndBox is null || SetEndButton is null) return;
        var enabled = UseEndCheck.IsChecked == true;
        EndBox.IsEnabled = enabled;
        SetEndButton.IsEnabled = enabled;
    }

    private void TimingBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitTimingBox(box); e.Handled = true;
    }
    private void TimingBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (sender is TextBox box) CommitTimingBox(box); }
    private void TransitionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTransitionSettings(); e.Handled = true;
    }
    private void TransitionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTransitionSettings();
    private void TransitionSetting_Changed(object sender, SelectionChangedEventArgs e) { if (!_loadingUi) CommitTransitionSettings(); }

    private void StartSimultaneously_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || _activeClip is null) return;
        _activeClip.TransitionStart = StartSimultaneouslyCheck.IsChecked == true
            ? _activeClip.Start
            : Math.Clamp(_activeClip.Cursor, _activeClip.Start, _activeClip.End);
        TemplateCombo.SelectedIndex = 3;
        UpdateActiveUi();
    }

    private void ClipVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi || _activeClip is null) return;
        _activeClip.VolumeDb = Math.Round(e.NewValue * 2) / 2;
        _loadingUi = true;
        ClipVolumeBox.Text = FormatSignedNumber(_activeClip.VolumeDb);
        _loadingUi = false;
        TemplateCombo.SelectedIndex = 3;
    }

    private void ClipVolumeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitClipVolume();
        e.Handled = true;
    }

    private void ClipVolumeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitClipVolume();

    private void CommitClipVolume()
    {
        if (_loadingUi || _activeClip is null) return;
        if (TryParseNumber(ClipVolumeBox.Text, out var value))
            _activeClip.VolumeDb = Math.Clamp(value, -24, 12);
        _loadingUi = true;
        ClipVolumeSlider.Value = _activeClip.VolumeDb;
        ClipVolumeBox.Text = FormatSignedNumber(_activeClip.VolumeDb);
        _loadingUi = false;
    }

    private void CommitTimingBox(TextBox box)
    {
        if (_activeClip is null || !TryParseTime(box.Text, out var value)) { UpdateActiveUi(); return; }
        if (ReferenceEquals(box, StartBox)) _activeClip.Start = Math.Clamp(value, 0, _activeClip.End);
        else if (ReferenceEquals(box, EndBox)) _activeClip.End = Math.Clamp(value, _activeClip.Start, _activeClip.Total);
        else if (ReferenceEquals(box, TransitionStartBox)) _activeClip.TransitionStart = Math.Clamp(value, _activeClip.Start, _activeClip.End);
        UpdateActiveUi();
    }

    private void CommitTransitionSettings()
    {
        if (_loadingUi || _activeClip is null) return;
        if (TryParseNumber(FadeOutBox.Text, out var fadeOut)) _activeClip.FadeOut = Math.Clamp(fadeOut, 0, 30);
        if (TryParseNumber(FadeInBox.Text, out var fadeIn)) _activeClip.FadeIn = Math.Clamp(fadeIn, 0, 30);
        _activeClip.TransitionMode = ((TransitionModeCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "SequentialFade" => JingleMergeService.TransitionMode.SequentialFade,
            "MixSound" => JingleMergeService.TransitionMode.MixSound,
            _ => JingleMergeService.TransitionMode.Crossfade
        };
        FadeOutBox.IsEnabled = _activeClip.TransitionMode != JingleMergeService.TransitionMode.MixSound;
        TemplateCombo.SelectedIndex = 3;
        UpdateActiveUi();
    }

    private void CommitActiveClip()
    {
        if (_activeClip is null || _loadingUi) return;
        CommitTimingBox(StartBox);
        if (_activeClip.UseEnd) CommitTimingBox(EndBox);
        CommitTimingBox(TransitionStartBox);
        CommitTransitionSettings();
        CommitClipVolume();
    }

    private void UpdateTransitionExplanation()
    {
        if (_activeClip is null || TransitionExplanationText is null) return;
        var current = Clips.IndexOf(_activeClip) + 1;
        var next = current + 1;
        TransitionGroup.Header = $"Övergång: ljud {current} → ljud {next}";
        TransitionExplanationText.Text = _activeClip.TransitionMode switch
        {
            JingleMergeService.TransitionMode.SequentialFade =>
                $"Den orange linjen i vågformen är övergångspunkten. Där tonas ljud {current} ut helt. Därefter startar ljud {next} och tonas in. Ljuden överlappar inte.",
            JingleMergeService.TransitionMode.MixSound =>
                $"Ljud {next} läggs som ett lager ovanpå ljud {current}, som fortsätter på oförändrad nivå. Välj samtidig start för flera parallella lager, exempelvis siren och publikljud ovanpå en mållåt. Endast ljud {next} tonas in.",
            _ =>
                $"Den orange linjen i vågformen är övergångspunkten. Där börjar ljud {current} tonas ut samtidigt som ljud {next} tonas in. Fade out och fade in kan ha olika längd."
        };
    }

    private void UpdateActiveUi()
    {
        if (_activeClip is null || StartBox is null) return;
        _loadingUi = true;
        _activeClip.Start = Math.Clamp(_activeClip.Start, 0, _activeClip.Total);
        _activeClip.End = Math.Clamp(_activeClip.End, _activeClip.Start, _activeClip.Total);
        _activeClip.TransitionStart = Math.Clamp(_activeClip.TransitionStart, _activeClip.Start, _activeClip.End);
        StartBox.Text = FormatTime(_activeClip.Start);
        EndBox.Text = FormatTime(_activeClip.End);
        TransitionStartBox.Text = FormatTime(_activeClip.TransitionStart);
        FadeOutBox.Text = FormatNumber(_activeClip.FadeOut);
        FadeInBox.Text = FormatNumber(_activeClip.FadeIn);
        TransitionModeCombo.SelectedIndex = _activeClip.TransitionMode switch
        {
            JingleMergeService.TransitionMode.SequentialFade => 1,
            JingleMergeService.TransitionMode.MixSound => 2,
            _ => 0
        };
        var isMixSound = _activeClip.TransitionMode == JingleMergeService.TransitionMode.MixSound;
        FadeOutPanel.Visibility = isMixSound ? Visibility.Collapsed : Visibility.Visible;
        StartSimultaneouslyCheck.Visibility = isMixSound ? Visibility.Visible : Visibility.Collapsed;
        StartSimultaneouslyCheck.IsChecked = isMixSound && Math.Abs(_activeClip.TransitionStart - _activeClip.Start) < .001;
        ClipVolumeLabel.Text = $"Ljudnivå ljud {Clips.IndexOf(_activeClip) + 1} (dB)";
        FadeOutLabel.Text = $"Fade out ljud {Clips.IndexOf(_activeClip) + 1}";
        FadeInLabel.Text = $"Fade in ljud {Clips.IndexOf(_activeClip) + 2}";
        ClipVolumeSlider.Value = _activeClip.VolumeDb;
        ClipVolumeBox.Text = FormatSignedNumber(_activeClip.VolumeDb);
        UpdateTransitionExplanation();
        CursorText.Text = $"Markör {FormatTime(_activeClip.Cursor)}";
        Waveform.StartFraction = Fraction(_activeClip.Start, _activeClip.Total);
        Waveform.EndFraction = _activeClip.UseEnd ? Fraction(_activeClip.End, _activeClip.Total) : 1;
        Waveform.PositionFraction = Fraction(_activeClip.Cursor, _activeClip.Total);
        Waveform.TransitionFraction = Clips.IndexOf(_activeClip) < Clips.Count - 1
            ? Fraction(_activeClip.TransitionStart, _activeClip.Total)
            : double.NaN;
        _loadingUi = false;
    }

    private void PlayPreview_Click(object sender, RoutedEventArgs e) => StartPreview();

    private void StartPreview()
    {
        var clip = _activeClip;
        if (clip is null || !File.Exists(clip.Choice.Jingle.FilePath)) return;
        var end = clip.UseEnd ? clip.End : EffectiveEnd(clip.Choice.Jingle, clip.Total);
        var cursor = clip.Cursor < clip.Start || clip.Cursor >= end ? clip.Start : clip.Cursor;
        DisposePreview();
        _previewClip = clip;
        _previewStart = cursor;
        _previewEnd = end;
        _previewReader = new AudioFileReader(clip.Choice.Jingle.FilePath) { CurrentTime = TimeSpan.FromSeconds(cursor) };
        ISampleProvider source = _previewReader;
        var jingle = clip.Choice.Jingle;
        if (Math.Abs(jingle.PitchSemitones) >= .01)
            source = new SmbPitchShiftingSampleProvider(source) { PitchFactor = (float)Math.Pow(2, jingle.PitchSemitones / 12) };
        var gainDb = jingle.GainDb + (jingle.NormalizationEnabled ? jingle.NormalizationGainDb : 0)
            + clip.VolumeDb + _viewModel.Settings.MasterVolumeDb;
        var volume = new VolumeSampleProvider(source) { Volume = (float)Math.Clamp(Math.Pow(10, gainDb / 20), 0, 4) };
        var effects = new DjEffectsSampleProvider(volume, jingle, _viewModel.Settings.MasterLimiterCeilingDbtp, _viewModel.Settings.MasterLimiterEnabled);
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
            _previewStart = Math.Min(_previewEnd, _previewStart + _previewClock.Elapsed.TotalSeconds);
            _previewClock.Reset(); _previewOutput.Pause(); _previewTimer.Stop(); UpdatePreviewUi(_previewStart);
        }
        else if (_previewOutput?.PlaybackState == PlaybackState.Paused)
        {
            _previewOutput.Play(); _previewClock.Restart(); _previewTimer.Start();
        }
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e) => DisposePreview();

    private async void PreviewAll_Click(object sender, RoutedEventArgs e)
    {
        CommitActiveClip();
        if (!TryBuildMergeRequest(out var segments, out var transitions)) return;
        DisposePreview();
        PreviewAllButton.IsEnabled = false;
        StatusText.Text = "Bygger förhandslyssning…";
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "FloorballDJ", "MergePreview");
            Directory.CreateDirectory(folder);
            _previewTempPath = Path.Combine(folder, $"preview-{Guid.NewGuid():N}.wav");
            await _merge.MergeManyAsync(segments, transitions, _previewTempPath);
            _previewReader = new AudioFileReader(_previewTempPath);
            _previewStart = 0;
            _previewEnd = _previewReader.TotalTime.TotalSeconds;
            _previewingComposition = true;
            var previewVolume = new VolumeSampleProvider(_previewReader)
            {
                Volume = (float)Math.Clamp(Math.Pow(10, _viewModel.Settings.MasterVolumeDb / 20), 0, 4)
            };
            _previewOutput = new WaveOutEvent();
            _previewOutput.Init(previewVolume.ToWaveProvider());
            _previewOutput.Play();
            _previewClock.Restart();
            _previewTimer.Start();
            UpdatePreviewUi(0);
        }
        catch (Exception ex)
        {
            DisposePreview();
            StatusText.Text = "Förhandslyssningen kunde inte skapas.";
            MessageBox.Show(this, ex.Message, "Förhandslyssning misslyckades", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PreviewAllButton.IsEnabled = true;
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_previewOutput is null) return;
        var position = Math.Min(_previewEnd, _previewStart + _previewClock.Elapsed.TotalSeconds);
        UpdatePreviewUi(position);
        if (position >= _previewEnd || _previewOutput.PlaybackState == PlaybackState.Stopped) DisposePreview();
    }
    private void UpdatePreviewUi(double position)
    {
        if (_previewingComposition)
        {
            StatusText.Text = $"Förhandslyssnar på hela mixen  {FormatTime(position)} / {FormatTime(_previewEnd)}";
            return;
        }
        if (_previewClip is null) return;
        _previewClip.Cursor = position;
        if (ReferenceEquals(_activeClip, _previewClip))
        {
            Waveform.PositionFraction = Fraction(position, _previewClip.Total);
            CursorText.Text = $"Markör {FormatTime(position)}";
            PreviewText.Text = $"{FormatTime(position)} / {FormatTime(_previewEnd)}";
        }
    }
    private void DisposePreview()
    {
        _previewTimer?.Stop(); _previewClock.Reset();
        try { _previewOutput?.Stop(); } catch { }
        _previewOutput?.Dispose(); _previewReader?.Dispose();
        _previewOutput = null; _previewReader = null; _previewClip = null; _previewingComposition = false;
        if (PreviewText is not null) PreviewText.Text = "";
        if (!string.IsNullOrWhiteSpace(_previewTempPath))
        {
            try { File.Delete(_previewTempPath); } catch { }
            _previewTempPath = null;
        }
    }

    private string? PromptForOutputPath()
    {
        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var outputFolder = string.IsNullOrWhiteSpace(musicFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloorballDJ", "Skapade jinglar")
            : Path.Combine(musicFolder, "FloorballDJ", "Skapade jinglar");
        Directory.CreateDirectory(outputFolder);
        var suggestedTitle = Clips.Count > 0 ? $"{Clips[0].Choice.Jingle.Title} – mix" : "Ny kombinerad jingle";
        var dialog = new SaveFileDialog
        {
            Title = "Namnge och spara den skapade jinglen",
            Filter = "Okomprimerat WAV-ljud|*.wav",
            DefaultExt = ".wav",
            AddExtension = true,
            InitialDirectory = outputFolder,
            FileName = SafeFileName(suggestedTitle) + ".wav"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CommitActiveClip();
        if (!TryBuildMergeRequest(out var segments, out var transitions)) return;
        var outputPath = PromptForOutputPath();
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        var outputTitle = Path.GetFileNameWithoutExtension(outputPath);
        CreateButton.IsEnabled = false;
        StatusText.Text = $"Skapar WAV-fil av {Clips.Count} ljud…";
        try
        {
            DisposePreview();
            await _merge.MergeManyAsync(segments, transitions, outputPath);
            using var reader = new AudioFileReader(outputPath);
            var targetDeck = _viewModel.SelectedDeck ?? _viewModel.Decks.First();
            var target = targetDeck.Jingles.FirstOrDefault(jingle => !jingle.HasContent);
            if (target is null)
            {
                targetDeck.Rows++; _viewModel.ApplyLayout(); target = targetDeck.Jingles.First(jingle => !jingle.HasContent);
            }
            target.Title = outputTitle;
            target.FilePath = outputPath; target.StartSeconds = 0; target.EndSeconds = null; target.DurationSeconds = reader.TotalTime.TotalSeconds;
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

    private bool TryBuildMergeRequest(out JingleMergeService.Segment[] segments,
        out JingleMergeService.Transition[] transitions)
    {
        segments = [];
        transitions = [];
        if (Clips.Count < 2 || Clips.Any(clip => !File.Exists(clip.Choice.Jingle.FilePath)))
        {
            MessageBox.Show(this, "Välj minst två tillgängliga ljud.");
            return false;
        }
        segments = Clips.Select(clip => new JingleMergeService.Segment(
            clip.Choice.Jingle, clip.Start, clip.UseEnd ? clip.End : null, clip.VolumeDb)).ToArray();
        transitions = Clips.Take(Clips.Count - 1).Select(clip => new JingleMergeService.Transition(
            clip.TransitionStart, clip.FadeOut, clip.FadeIn, clip.TransitionMode)).ToArray();
        return true;
    }

    private void RefreshTabTitles()
    {
        for (var index = 0; index < Clips.Count; index++) Clips[index].TabTitle = $"{index + 1} · {Clips[index].Choice.Jingle.Title}";
    }
    private static double ReadDuration(string path) { using var reader = new AudioFileReader(path); return reader.TotalTime.TotalSeconds; }
    private static double EffectiveEnd(Jingle jingle, double total) => Math.Clamp(jingle.EndSeconds ?? total, 0, total);
    private static double Fraction(double seconds, double total) => total <= 0 ? 0 : Math.Clamp(seconds / total, 0, 1);
    private static double PositionToSeconds(MouseButtonEventArgs e, FrameworkElement surface, double total) =>
        Math.Clamp(e.GetPosition(surface).X / Math.Max(1, surface.ActualWidth), 0, 1) * total;
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff");
    private static string FormatNumber(double value) => value.ToString("0.0##", CultureInfo.CurrentCulture);
    private static string FormatSignedNumber(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
    private static string SafeFileName(string value) => string.Concat((value.Trim().Length == 0 ? "Kombinerad jingle" : value.Trim())
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
