using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FloorballDJ.Models;

public enum JinglePlayMode { Mix, Solo, Duck }

public sealed class AppSettings
{
    public int DeckCount { get; set; } = 4;
    public int Rows { get; set; } = 4;
    public int Columns { get; set; } = 5;
    public double ButtonHeight { get; set; } = 120;
    public double ButtonWidth { get; set; } = 220;
    public double TitleFontSize { get; set; } = 15;
    public string FontFamily { get; set; } = "Segoe UI Variable Display";
    public string? OutputDeviceId { get; set; }
    public string? SecondaryOutputDeviceId { get; set; }
    public string? MusicFolderPath { get; set; }
    public double MasterVolumeDb { get; set; } = -27;
    public double FadeInSeconds { get; set; } = 1.5;
    public double FadeOutSeconds { get; set; } = 2.5;
    public double AutoplayTransitionSeconds { get; set; } = 4;
    public double DuckLevelDb { get; set; } = -12;
    public double TalkDuckLevelDb { get; set; } = -15;
    public double DefaultLoudnessTargetLufs { get; set; } = -16;
    public bool MasterLimiterEnabled { get; set; } = true;
    public double MasterLimiterCeilingDbtp { get; set; } = -1;
    public bool AutoMixHeadroomEnabled { get; set; } = true;
    public bool TrackSession { get; set; }
}

public sealed class Jingle : INotifyPropertyChanged
{
    private string _title = "";
    private string _filePath = "";
    private int _position;
    private string _buttonColor = "#182338";
    private string _textColor = "#F7FAFC";
    private bool _isTextBlock;
    private double _startSeconds;
    private double? _endSeconds;
    private double _durationSeconds;
    private JinglePlayMode _playMode = JinglePlayMode.Solo;
    private bool _loop;
    private bool _allowMultipleClicks;
    private double _gainDb;
    private double _pitchSemitones;
    private double _tempoPercent;
    private double _ratePercent;
    private double? _fadeInOverrideSeconds;
    private double? _fadeOutOverrideSeconds;
    private string? _shortcut;
    private bool _shortcutSwitchesDeck;
    private string _category = "";
    private string? _categoryShortcut;
    private int _sessionPlayCount;
    private int _queuePosition;
    private int _autoplayQueuePosition;
    private bool _normalizationEnabled;
    private double _normalizationTargetLufs = -16;
    private double? _integratedLufs;
    private double? _truePeakDbtp;
    private double? _loudnessRangeLu;
    private double? _maxMomentaryLufs;
    private double _normalizationGainDb;
    private long _analysisFileSize;
    private long _analysisFileWriteUtcTicks;
    private double _eqLowDb;
    private double _eqMidDb;
    private double _eqHighDb;
    private bool _compressorEnabled;
    private double _compressorThresholdDb = -12;
    private double _compressorRatio = 3;
    private double _compressorAttackMs = 10;
    private double _compressorReleaseMs = 120;
    private bool _isSearchMatch;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get => _title; set => Set(ref _title, value); }
    public string FilePath { get => _filePath; set { if (Set(ref _filePath, value)) { Raise(nameof(HasAudio)); Raise(nameof(HasContent)); Raise(nameof(IsMissing)); Raise(nameof(HasFreshLoudnessAnalysis)); } } }
    public int Position { get => _position; set => Set(ref _position, value); }
    public string ButtonColor { get => _buttonColor; set => Set(ref _buttonColor, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public bool IsTextBlock { get => _isTextBlock; set { if (Set(ref _isTextBlock, value)) Raise(nameof(HasContent)); } }
    public double StartSeconds { get => _startSeconds; set => Set(ref _startSeconds, value); }
    public double? EndSeconds { get => _endSeconds; set => Set(ref _endSeconds, value); }
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }
    public JinglePlayMode PlayMode { get => _playMode; set => Set(ref _playMode, value); }
    public bool Loop { get => _loop; set => Set(ref _loop, value); }
    public bool AllowMultipleClicks { get => _allowMultipleClicks; set => Set(ref _allowMultipleClicks, value); }
    public double GainDb { get => _gainDb; set => Set(ref _gainDb, value); }
    public double PitchSemitones { get => _pitchSemitones; set => Set(ref _pitchSemitones, value); }
    public double TempoPercent { get => _tempoPercent; set => Set(ref _tempoPercent, value); }
    public double RatePercent { get => _ratePercent; set => Set(ref _ratePercent, value); }
    public double? FadeInOverrideSeconds { get => _fadeInOverrideSeconds; set => Set(ref _fadeInOverrideSeconds, value); }
    public double? FadeOutOverrideSeconds { get => _fadeOutOverrideSeconds; set => Set(ref _fadeOutOverrideSeconds, value); }
    public string? Shortcut { get => _shortcut; set => Set(ref _shortcut, value); }
    public bool ShortcutSwitchesDeck { get => _shortcutSwitchesDeck; set => Set(ref _shortcutSwitchesDeck, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string? CategoryShortcut { get => _categoryShortcut; set => Set(ref _categoryShortcut, value); }
    public int SessionPlayCount { get => _sessionPlayCount; set => Set(ref _sessionPlayCount, value); }
    public bool NormalizationEnabled { get => _normalizationEnabled; set => Set(ref _normalizationEnabled, value); }
    public double NormalizationTargetLufs { get => _normalizationTargetLufs; set => Set(ref _normalizationTargetLufs, value); }
    public double? IntegratedLufs { get => _integratedLufs; set { if (Set(ref _integratedLufs, value)) Raise(nameof(HasFreshLoudnessAnalysis)); } }
    public double? TruePeakDbtp { get => _truePeakDbtp; set { if (Set(ref _truePeakDbtp, value)) Raise(nameof(HasFreshLoudnessAnalysis)); } }
    public double? LoudnessRangeLu { get => _loudnessRangeLu; set => Set(ref _loudnessRangeLu, value); }
    public double? MaxMomentaryLufs { get => _maxMomentaryLufs; set => Set(ref _maxMomentaryLufs, value); }
    public double NormalizationGainDb { get => _normalizationGainDb; set => Set(ref _normalizationGainDb, value); }
    public long AnalysisFileSize { get => _analysisFileSize; set { if (Set(ref _analysisFileSize, value)) Raise(nameof(HasFreshLoudnessAnalysis)); } }
    public long AnalysisFileWriteUtcTicks { get => _analysisFileWriteUtcTicks; set { if (Set(ref _analysisFileWriteUtcTicks, value)) Raise(nameof(HasFreshLoudnessAnalysis)); } }
    public double EqLowDb { get => _eqLowDb; set => Set(ref _eqLowDb, value); }
    public double EqMidDb { get => _eqMidDb; set => Set(ref _eqMidDb, value); }
    public double EqHighDb { get => _eqHighDb; set => Set(ref _eqHighDb, value); }
    public bool CompressorEnabled { get => _compressorEnabled; set => Set(ref _compressorEnabled, value); }
    public double CompressorThresholdDb { get => _compressorThresholdDb; set => Set(ref _compressorThresholdDb, value); }
    public double CompressorRatio { get => _compressorRatio; set => Set(ref _compressorRatio, value); }
    public double CompressorAttackMs { get => _compressorAttackMs; set => Set(ref _compressorAttackMs, value); }
    public double CompressorReleaseMs { get => _compressorReleaseMs; set => Set(ref _compressorReleaseMs, value); }
    [JsonIgnore] public bool HasFreshLoudnessAnalysis
    {
        get
        {
            if (!HasAudio || IntegratedLufs is null || TruePeakDbtp is null) return false;
            try { var file = new FileInfo(FilePath); return file.Length == AnalysisFileSize && file.LastWriteTimeUtc.Ticks == AnalysisFileWriteUtcTicks; }
            catch { return false; }
        }
    }
    [JsonIgnore] public int QueuePosition { get => _queuePosition; set => Set(ref _queuePosition, value); }
    [JsonIgnore] public int AutoplayQueuePosition { get => _autoplayQueuePosition; set => Set(ref _autoplayQueuePosition, value); }
    [JsonIgnore] public bool HasAudio => !string.IsNullOrWhiteSpace(FilePath);
    [JsonIgnore] public bool HasContent => HasAudio || IsTextBlock;
    [JsonIgnore] public bool IsMissing => HasAudio && !File.Exists(FilePath);
    [JsonIgnore] public bool IsSearchMatch { get => _isSearchMatch; set => Set(ref _isSearchMatch, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Deck : INotifyPropertyChanged
{
    private string _name = "Deck";
    private int _rows;
    private int _columns;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public int Rows { get => _rows; set { if (_rows == value) return; _rows = value; Raise(); } }
    public int Columns { get => _columns; set { if (_columns == value) return; _columns = value; Raise(); } }
    public ObservableCollection<Jingle> Jingles { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FloorballProject
{
    public int FormatVersion { get; set; } = 2;
    public string Name { get; set; } = "Mitt sportevenemang";
    public AppSettings Settings { get; set; } = new();
    public ObservableCollection<Deck> Decks { get; set; } = [];
}

public sealed record OutputDevice(string Id, string Name);

public sealed record PlaybackSnapshot(
    Guid? JingleId,
    string Title,
    string FilePath,
    TimeSpan Position,
    TimeSpan Duration,
    float PeakLeftDb,
    float PeakRightDb,
    bool IsPlaying,
    bool IsPaused);
