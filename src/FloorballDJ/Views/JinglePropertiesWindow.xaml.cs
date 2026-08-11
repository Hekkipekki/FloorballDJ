using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Views;

public partial class JinglePropertiesWindow : Window
{
    private const double DetailWindowSeconds = 10;
    private readonly Jingle _target;
    private readonly double _masterVolumeDb;
    private readonly AudioAnalysisService _analysisService = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly Stopwatch _previewClock = new();
    private string _path;
    private double _totalSeconds;
    private double _start;
    private double _end;
    private double _cursorSeconds;
    private bool _scrubbing;
    private double _dragStartX;
    private double _dragStartSeconds;
    private double _previewClockStartSeconds;
    private string? _shortcut;
    private string? _categoryShortcut;
    private AudioFileReader? _previewReader;
    private WaveOutEvent? _previewOutput;
    private VolumeSampleProvider? _previewVolume;
    private LoudnessAnalysis? _analysis;

    public JinglePropertiesWindow(Jingle jingle, IReadOnlyList<OutputDevice> _, double masterVolumeDb = 0)
    {
        InitializeComponent();
        _target = jingle;
        _masterVolumeDb = masterVolumeDb;
        _path = jingle.FilePath;
        _totalSeconds = jingle.DurationSeconds;
        _start = jingle.StartSeconds;
        _end = jingle.EndSeconds ?? Math.Max(_totalSeconds, 0);
        _cursorSeconds = _start;
        TitleBox.Text = jingle.Title;
        PathBox.Text = jingle.FilePath;
        GainSlider.Value = jingle.GainDb;
        GainBox.Text = jingle.GainDb.ToString("0.0", CultureInfo.InvariantCulture);
        PitchSlider.Value = jingle.PitchSemitones;
        PitchBox.Text = jingle.PitchSemitones.ToString("0.0", CultureInfo.InvariantCulture);
        TempoSlider.Value = jingle.TempoPercent;
        TempoBox.Text = jingle.TempoPercent.ToString("0.0", CultureInfo.InvariantCulture);
        RateSlider.Value = jingle.RatePercent;
        RateBox.Text = jingle.RatePercent.ToString("0.0", CultureInfo.InvariantCulture);
        EqLowSlider.Value = jingle.EqLowDb; EqLowBox.Text = FormatNumber(jingle.EqLowDb);
        EqMidSlider.Value = jingle.EqMidDb; EqMidBox.Text = FormatNumber(jingle.EqMidDb);
        EqHighSlider.Value = jingle.EqHighDb; EqHighBox.Text = FormatNumber(jingle.EqHighDb);
        CompressorCheck.IsChecked = jingle.CompressorEnabled;
        CompressorThresholdSlider.Value = jingle.CompressorThresholdDb; CompressorThresholdBox.Text = FormatNumber(jingle.CompressorThresholdDb);
        CompressorRatioSlider.Value = jingle.CompressorRatio; CompressorRatioBox.Text = FormatNumber(jingle.CompressorRatio);
        CompressorAttackBox.Text = FormatNumber(jingle.CompressorAttackMs);
        CompressorReleaseBox.Text = FormatNumber(jingle.CompressorReleaseMs);
        NormalizationCheck.IsChecked = jingle.NormalizationEnabled;
        TargetLufsBox.Text = FormatNumber(jingle.NormalizationTargetLufs is < -30 or > 0 ? -16 : jingle.NormalizationTargetLufs);
        if (jingle.IntegratedLufs is double integrated && jingle.TruePeakDbtp is double truePeak)
            _analysis = new LoudnessAnalysis(integrated, truePeak, jingle.LoudnessRangeLu ?? 0, jingle.MaxMomentaryLufs ?? -70,
                DateTimeOffset.MinValue, jingle.AnalysisFileSize, jingle.AnalysisFileWriteUtcTicks);
        PlayModeCombo.ItemsSource = Enum.GetValues<JinglePlayMode>();
        PlayModeCombo.SelectedItem = jingle.PlayMode;
        LoopCheck.IsChecked = jingle.Loop;
        MultipleClicksCheck.IsChecked = jingle.AllowMultipleClicks;
        FadeInBox.Text = FormatNullable(jingle.FadeInOverrideSeconds);
        FadeOutBox.Text = FormatNullable(jingle.FadeOutOverrideSeconds);
        _shortcut = ShortcutService.Normalize(jingle.Shortcut);
        ShortcutSwitchesDeckCheck.IsChecked = jingle.ShortcutSwitchesDeck;
        CategoryBox.Text = jingle.Category;
        _categoryShortcut = ShortcutService.Normalize(jingle.CategoryShortcut);
        UpdateShortcutText();
        UpdateCategoryShortcutText();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _previewTimer.Tick += PreviewTimer_Tick;
        Loaded += async (_, _) => await LoadWaveformAsync();
        Closed += (_, _) => DisposePreview();
        GainSlider.ValueChanged += (_, _) => { GainBox.Text = GainSlider.Value.ToString("0.0", CultureInfo.InvariantCulture); RefreshPreviewVolume(); };
        PitchSlider.ValueChanged += (_, _) => PitchBox.Text = PitchSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        TempoSlider.ValueChanged += (_, _) => TempoBox.Text = TempoSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        RateSlider.ValueChanged += (_, _) => RateBox.Text = RateSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        EqLowSlider.ValueChanged += (_, _) => EqLowBox.Text = FormatNumber(EqLowSlider.Value);
        EqMidSlider.ValueChanged += (_, _) => EqMidBox.Text = FormatNumber(EqMidSlider.Value);
        EqHighSlider.ValueChanged += (_, _) => EqHighBox.Text = FormatNumber(EqHighSlider.Value);
        CompressorThresholdSlider.ValueChanged += (_, _) => CompressorThresholdBox.Text = FormatNumber(CompressorThresholdSlider.Value);
        CompressorRatioSlider.ValueChanged += (_, _) => CompressorRatioBox.Text = FormatNumber(CompressorRatioSlider.Value);
        GainBox.TextChanged += (_, _) => RefreshPreviewVolume();
        NormalizationCheck.Checked += (_, _) => RefreshPreviewVolume();
        NormalizationCheck.Unchecked += (_, _) => RefreshPreviewVolume();
        TargetLufsBox.TextChanged += (_, _) => { UpdateLoudnessUi(); RefreshPreviewVolume(); };
        UpdateLoudnessUi();
        UpdateTiming();
    }

    private async Task LoadWaveformAsync()
    {
        ResetTechnicalInfo();
        if (File.Exists(_path))
        {
            try
            {
                using var reader = new AudioFileReader(_path);
                _totalSeconds = reader.TotalTime.TotalSeconds;
                if (_end <= 0 || _end > _totalSeconds) _end = _totalSeconds;
                _start = Math.Clamp(_start, 0, _end);
                _cursorSeconds = _start;
                UpdateTechnicalInfo(reader, _path);
            }
            catch { }
        }
        await Task.WhenAll(FullWaveform.LoadAsync(_path), DetailWaveform.LoadAsync(_path));
        UpdateDetailView();
        UpdateTiming();
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_previewReader is null) return;
        if (_previewClock.IsRunning) _cursorSeconds = _previewClockStartSeconds + _previewClock.Elapsed.TotalSeconds;
        if (_cursorSeconds >= _end)
        {
            _cursorSeconds = _end;
            DisposePreview();
        }
        UpdatePlaybackUi();
    }

    private async void AnalyzeLoudness_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        AnalyzeLoudnessButton.IsEnabled = false;
        LoudnessStatusText.Text = "Analyserar…";
        try
        {
            _analysis = await _analysisService.AnalyzeAsync(_path, _start, _end);
            NormalizationCheck.IsChecked = true;
            UpdateLoudnessUi();
        }
        catch (Exception ex)
        {
            LoudnessStatusText.Text = "Analysen misslyckades";
            MessageBox.Show(this, ex.Message, "Kunde inte analysera loudness", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { AnalyzeLoudnessButton.IsEnabled = true; }
    }

    private void UpdateLoudnessUi()
    {
        if (_analysis is null)
        {
            IntegratedLufsText.Text = "– LUFS";
            TruePeakText.Text = "– dBTP";
            SuggestedGainText.Text = "– dB";
            LoudnessStatusText.Text = "Inte analyserad";
            return;
        }
        var target = Math.Clamp(ParseNumber(TargetLufsBox.Text, -16), -30, 0);
        var gain = _analysis.SuggestedGain(target, -1);
        IntegratedLufsText.Text = $"{_analysis.IntegratedLufs:0.0} LUFS";
        TruePeakText.Text = $"{_analysis.TruePeakDbtp:0.0} dBTP";
        SuggestedGainText.Text = $"{gain:+0.0;-0.0;0.0} dB";
        LoudnessStatusText.Text = _target.HasFreshLoudnessAnalysis || _analysis.AnalyzedAt > DateTimeOffset.MinValue
            ? $"Klar • LRA {_analysis.LoudnessRangeLu:0.0} LU"
            : "Tidigare analys – filen kan ha ändrats";
    }

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = true;
        if (sender is UIElement element) element.CaptureMouse();
        if (ReferenceEquals(sender, FullWaveformSurface)) SeekFromFullWaveform(e);
        else
        {
            _dragStartX = e.GetPosition(DetailWaveform).X;
            _dragStartSeconds = _cursorSeconds;
        }
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing || e.LeftButton != MouseButtonState.Pressed) return;
        if (ReferenceEquals(sender, FullWaveformSurface)) SeekFromFullWaveform(e);
        else DragDetailWaveform(e);
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, FullWaveformSurface)) SeekFromFullWaveform(e);
        else DragDetailWaveform(e);
        _scrubbing = false;
        if (sender is UIElement element) element.ReleaseMouseCapture();
    }

    private void SeekFromFullWaveform(MouseEventArgs e)
    {
        if (_totalSeconds <= 0) return;
        var fraction = Math.Clamp(e.GetPosition(FullWaveform).X / Math.Max(1, FullWaveform.ActualWidth), 0, 1);
        _cursorSeconds = fraction * _totalSeconds;
        if (_previewReader is not null) _previewReader.CurrentTime = TimeSpan.FromSeconds(_cursorSeconds);
        ResetPreviewClockIfPlaying();
        UpdatePlaybackUi();
    }

    private void DragDetailWaveform(MouseEventArgs e)
    {
        if (_totalSeconds <= 0) return;
        var deltaPixels = e.GetPosition(DetailWaveform).X - _dragStartX;
        var deltaSeconds = deltaPixels / Math.Max(1, DetailWaveform.ActualWidth) * DetailWindowSeconds;
        _cursorSeconds = Math.Clamp(_dragStartSeconds - deltaSeconds, 0, _totalSeconds);
        if (_previewReader is not null) _previewReader.CurrentTime = TimeSpan.FromSeconds(_cursorSeconds);
        ResetPreviewClockIfPlaying();
        UpdatePlaybackUi();
    }

    private void UpdateDetailView()
    {
        if (DetailWaveform is null || _totalSeconds <= 0) return;
        var halfWindowFraction = DetailWindowSeconds / _totalSeconds / 2;
        var cursorFraction = _cursorSeconds / _totalSeconds;
        DetailWaveform.ViewStartFraction = cursorFraction - halfWindowFraction;
        DetailWaveform.ViewEndFraction = cursorFraction + halfWindowFraction;
    }

    private void SetStart_Click(object sender, RoutedEventArgs e) { _start = Math.Min(_cursorSeconds, _end); UpdateTiming(); }
    private void SetEnd_Click(object sender, RoutedEventArgs e) { _end = Math.Max(_cursorSeconds, _start); UpdateTiming(); }
    private void StartMinus_Click(object sender, RoutedEventArgs e) => AdjustStart(-AdjustmentStep());
    private void StartPlus_Click(object sender, RoutedEventArgs e) => AdjustStart(AdjustmentStep());
    private void EndMinus_Click(object sender, RoutedEventArgs e) => AdjustEnd(-AdjustmentStep());
    private void EndPlus_Click(object sender, RoutedEventArgs e) => AdjustEnd(AdjustmentStep());
    private static double AdjustmentStep() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .01 : .1;
    private void AdjustStart(double delta) { _start = Math.Clamp(_start + delta, 0, _end); _cursorSeconds = _start; UpdateTiming(); }
    private void AdjustEnd(double delta) { _end = Math.Clamp(_end + delta, _start, _totalSeconds); _cursorSeconds = _end; UpdateTiming(); }

    private void StartText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTime(StartText, true);
    private void EndText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTime(EndText, false);
    private void TimeText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitTime(box, ReferenceEquals(box, StartText));
        e.Handled = true;
    }

    private void CommitTime(TextBox box, bool isStart)
    {
        if (!TryParseTime(box.Text, out var seconds)) { box.Text = FormatPrecise(isStart ? _start : _end); return; }
        if (isStart) _start = Math.Clamp(seconds, 0, _end);
        else _end = Math.Clamp(seconds, _start, _totalSeconds);
        _cursorSeconds = isStart ? _start : _end;
        UpdateTiming();
    }

    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim().Replace(',', '.');
        if (!text.Contains(':')) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return false;
        var values = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        seconds = values.Length == 2 ? values[0] * 60 + values[1] : values[0] * 3600 + values[1] * 60 + values[2];
        return seconds >= 0;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Ljudfiler|*.mp3;*.wav;*.aiff;*.wma;*.m4a;*.aac;*.flac;*.mp4|Alla filer|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        DisposePreview();
        _path = dialog.FileName;
        _analysis = null;
        NormalizationCheck.IsChecked = false;
        UpdateLoudnessUi();
        PathBox.Text = _path;
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Text = Path.GetFileNameWithoutExtension(_path);
        _start = 0; _end = 0; _cursorSeconds = 0;
        await LoadWaveformAsync();
    }

    private void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        if (_cursorSeconds < _start || _cursorSeconds >= _end) _cursorSeconds = _start;
        DisposePreview();
        _previewReader = new AudioFileReader(_path) { CurrentTime = TimeSpan.FromSeconds(_cursorSeconds) };
        var mixerSettings = BuildMixerSettings();
        ISampleProvider source = _previewReader;
        if (Math.Abs(mixerSettings.PitchSemitones) >= .01)
            source = new SmbPitchShiftingSampleProvider(source) { PitchFactor = (float)Math.Pow(2, mixerSettings.PitchSemitones / 12) };
        _previewVolume = new VolumeSampleProvider(source)
        {
            Volume = (float)Math.Clamp(Math.Pow(10, (mixerSettings.GainDb +
                (mixerSettings.NormalizationEnabled ? mixerSettings.NormalizationGainDb : 0) +
                _masterVolumeDb) / 20), 0, 4)
        };
        var effects = new DjEffectsSampleProvider(_previewVolume, mixerSettings, -1);
        _previewOutput = new WaveOutEvent();
        _previewOutput.Init(effects.ToWaveProvider());
        _previewOutput.Play();
        _previewClockStartSeconds = _cursorSeconds;
        _previewClock.Restart();
        _previewTimer.Start();
    }

    private void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOutput?.PlaybackState == PlaybackState.Playing)
        {
            _cursorSeconds = Math.Min(_end, _previewClockStartSeconds + _previewClock.Elapsed.TotalSeconds);
            _previewClock.Stop();
            _previewOutput.Pause();
            _previewTimer.Stop();
            UpdatePlaybackUi();
        }
        else if (_previewOutput?.PlaybackState == PlaybackState.Paused)
        {
            _previewClockStartSeconds = _cursorSeconds;
            _previewClock.Restart();
            _previewOutput.Play();
            _previewTimer.Start();
        }
    }

    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        DisposePreview();
        _cursorSeconds = _start;
        UpdatePlaybackUi();
    }

    private async void TrimSilence_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        try
        {
            var bounds = await Task.Run(() => DetectAudibleBounds(_path, 0.001f));
            _start = bounds.start; _end = bounds.end; _cursorSeconds = _start; UpdateTiming();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kunde inte analysera ljudet", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { MessageBox.Show(this, "Ange en titel."); return; }
        var mixer = BuildMixerSettings();
        _target.Title = TitleBox.Text.Trim(); _target.FilePath = _path; _target.DurationSeconds = _totalSeconds;
        _target.StartSeconds = _start; _target.EndSeconds = _end < _totalSeconds ? _end : null;
        _target.GainDb = mixer.GainDb; _target.PitchSemitones = mixer.PitchSemitones;
        _target.TempoPercent = mixer.TempoPercent; _target.RatePercent = mixer.RatePercent;
        _target.EqLowDb = mixer.EqLowDb; _target.EqMidDb = mixer.EqMidDb; _target.EqHighDb = mixer.EqHighDb;
        _target.CompressorEnabled = mixer.CompressorEnabled; _target.CompressorThresholdDb = mixer.CompressorThresholdDb;
        _target.CompressorRatio = mixer.CompressorRatio; _target.CompressorAttackMs = mixer.CompressorAttackMs; _target.CompressorReleaseMs = mixer.CompressorReleaseMs;
        _target.NormalizationEnabled = mixer.NormalizationEnabled; _target.NormalizationTargetLufs = mixer.NormalizationTargetLufs;
        _target.NormalizationGainDb = mixer.NormalizationGainDb;
        if (_analysis is not null)
        {
            _target.IntegratedLufs = _analysis.IntegratedLufs; _target.TruePeakDbtp = _analysis.TruePeakDbtp;
            _target.LoudnessRangeLu = _analysis.LoudnessRangeLu; _target.MaxMomentaryLufs = _analysis.MaxMomentaryLufs;
            _target.AnalysisFileSize = _analysis.FileSize; _target.AnalysisFileWriteUtcTicks = _analysis.FileWriteUtcTicks;
        }
        _target.PlayMode = (JinglePlayMode)(PlayModeCombo.SelectedItem ?? JinglePlayMode.Solo);
        _target.Loop = LoopCheck.IsChecked == true;
        _target.AllowMultipleClicks = MultipleClicksCheck.IsChecked == true;
        _target.FadeInOverrideSeconds = ParseNullable(FadeInBox.Text); _target.FadeOutOverrideSeconds = ParseNullable(FadeOutBox.Text);
        _target.Shortcut = _shortcut;
        _target.ShortcutSwitchesDeck = ShortcutSwitchesDeckCheck.IsChecked == true;
        _target.Category = CategoryBox.Text.Trim();
        _target.CategoryShortcut = _categoryShortcut;
        DialogResult = true;
    }

    private Jingle BuildMixerSettings()
    {
        var targetLufs = Math.Clamp(ParseNumber(TargetLufsBox.Text, -16), -30, 0);
        return new Jingle
        {
            GainDb = Math.Clamp(ParseNumber(GainBox.Text, GainSlider.Value), -24, 12),
            PitchSemitones = Math.Clamp(ParseNumber(PitchBox.Text, PitchSlider.Value), -12, 12),
            TempoPercent = Math.Clamp(ParseNumber(TempoBox.Text, TempoSlider.Value), -50, 100),
            RatePercent = Math.Clamp(ParseNumber(RateBox.Text, RateSlider.Value), -50, 100),
            EqLowDb = Math.Clamp(ParseNumber(EqLowBox.Text, EqLowSlider.Value), -12, 12),
            EqMidDb = Math.Clamp(ParseNumber(EqMidBox.Text, EqMidSlider.Value), -12, 12),
            EqHighDb = Math.Clamp(ParseNumber(EqHighBox.Text, EqHighSlider.Value), -12, 12),
            CompressorEnabled = CompressorCheck.IsChecked == true,
            CompressorThresholdDb = Math.Clamp(ParseNumber(CompressorThresholdBox.Text, CompressorThresholdSlider.Value), -36, 0),
            CompressorRatio = Math.Clamp(ParseNumber(CompressorRatioBox.Text, CompressorRatioSlider.Value), 1, 12),
            CompressorAttackMs = Math.Clamp(ParseNumber(CompressorAttackBox.Text, 10), .1, 200),
            CompressorReleaseMs = Math.Clamp(ParseNumber(CompressorReleaseBox.Text, 120), 10, 2000),
            NormalizationEnabled = NormalizationCheck.IsChecked == true && _analysis is not null,
            NormalizationTargetLufs = targetLufs,
            NormalizationGainDb = _analysis?.SuggestedGain(targetLufs, -1) ?? 0
        };
    }

    private void ChooseShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutCaptureWindow(_shortcut) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _shortcut = dialog.SelectedShortcut;
        UpdateShortcutText();
    }

    private void UpdateShortcutText() => ShortcutText.Text = _shortcut ?? "<Ingen>";

    private void ChooseCategoryShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutCaptureWindow(_categoryShortcut) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _categoryShortcut = dialog.SelectedShortcut;
        UpdateCategoryShortcutText();
    }

    private void UpdateCategoryShortcutText() => CategoryShortcutText.Text = _categoryShortcut ?? "Ingen slumpknapp";

    private void UpdateTiming()
    {
        _start = Math.Clamp(_start, 0, Math.Max(0, _totalSeconds));
        _end = Math.Clamp(_end, _start, Math.Max(_start, _totalSeconds));
        StartText.Text = FormatPrecise(_start); EndText.Text = FormatPrecise(_end);
        DurationText.Text = FormatPrecise(Math.Max(0, _end - _start));
        var startFraction = _totalSeconds <= 0 ? 0 : _start / _totalSeconds;
        var endFraction = _totalSeconds <= 0 ? 1 : _end / _totalSeconds;
        FullWaveform.StartFraction = DetailWaveform.StartFraction = startFraction;
        FullWaveform.EndFraction = DetailWaveform.EndFraction = endFraction;
        UpdatePlaybackUi();
    }

    private void UpdatePlaybackUi()
    {
        var fraction = _totalSeconds <= 0 ? 0 : Math.Clamp(_cursorSeconds / _totalSeconds, 0, 1);
        FullWaveform.PositionFraction = DetailWaveform.PositionFraction = fraction;
        UpdateDetailView();
        PreviewPositionText.Text = $"{FormatPrecise(_cursorSeconds)} / {FormatPrecise(_totalSeconds)}";
    }

    private void ResetPreviewClockIfPlaying()
    {
        if (_previewOutput?.PlaybackState != PlaybackState.Playing) return;
        _previewClockStartSeconds = _cursorSeconds;
        _previewClock.Restart();
    }

    private void UpdateTechnicalInfo(AudioFileReader reader, string path)
    {
        var info = new FileInfo(path);
        var bitrate = reader.WaveFormat.AverageBytesPerSecond * 8 / 1000;
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            try { using var mp3 = new Mp3FileReader(path); bitrate = mp3.Mp3WaveFormat.AverageBytesPerSecond * 8 / 1000; } catch { }
        FormatInfoText.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        FileSizeInfoText.Text = FormatBytes(info.Length); DurationInfoText.Text = MainViewModel.Format(reader.TotalTime);
        BitrateInfoText.Text = $"{bitrate:N0} kbit/s"; SampleRateInfoText.Text = $"{reader.WaveFormat.SampleRate:N0} Hz";
        ChannelsInfoText.Text = reader.WaveFormat.Channels switch { 1 => "Mono", 2 => "Stereo", var channels => $"{channels} kanaler" };
        EncodingInfoText.Text = $"{reader.WaveFormat.Encoding}, {reader.WaveFormat.BitsPerSample} bit";
        ModifiedInfoText.Text = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    }

    private void ResetTechnicalInfo() => FormatInfoText.Text = FileSizeInfoText.Text = DurationInfoText.Text = BitrateInfoText.Text =
        SampleRateInfoText.Text = ChannelsInfoText.Text = EncodingInfoText.Text = ModifiedInfoText.Text = "–";

    private static string FormatPrecise(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss\.fff") : time.ToString(@"mm\:ss\.fff");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"]; var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static (double start, double end) DetectAudibleBounds(string path, float threshold)
    {
        using var reader = new AudioFileReader(path); var buffer = new float[16384]; long sampleIndex = 0, first = -1, last = 0; int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++, sampleIndex++) if (Math.Abs(buffer[i]) >= threshold) { if (first < 0) first = sampleIndex; last = sampleIndex; }
        if (first < 0) return (0, reader.TotalTime.TotalSeconds);
        var samplesPerSecond = reader.WaveFormat.SampleRate * reader.WaveFormat.Channels;
        return (Math.Max(0, first / (double)samplesPerSecond - .01), Math.Min(reader.TotalTime.TotalSeconds, last / (double)samplesPerSecond + .01));
    }

    private void DisposePreview(bool stopTimer = true)
    {
        _previewClock.Stop();
        if (stopTimer) _previewTimer.Stop();
        _previewOutput?.Stop(); _previewOutput?.Dispose(); _previewReader?.Dispose();
        _previewOutput = null; _previewReader = null; _previewVolume = null;
    }

    private void RefreshPreviewVolume()
    {
        if (_previewVolume is null) return;
        var settings = BuildMixerSettings();
        var gainDb = settings.GainDb + (settings.NormalizationEnabled ? settings.NormalizationGainDb : 0) + _masterVolumeDb;
        _previewVolume.Volume = (float)Math.Clamp(Math.Pow(10, gainDb / 20), 0, 4);
    }

    private static string FormatNullable(double? x) => x?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private static string FormatNumber(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    private static double ParseNumber(string text, double fallback) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static double? ParseNullable(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
}
