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
    public string? AutoplayDefaultPlaylistPath { get; set; }
    public string? AutoplayShortcut { get; set; }
    public double AutoplayDefaultPlaylistVolumeDb { get; set; }
    public List<AutoplayProfile> AutoplayProfiles { get; set; } = [];
    public double DuckLevelDb { get; set; } = -12;
    public double TalkDuckLevelDb { get; set; } = -15;
    public double DefaultLoudnessTargetLufs { get; set; } = -16;
    public bool MasterLimiterEnabled { get; set; } = true;
    public double MasterLimiterCeilingDbtp { get; set; } = -1;
    public bool AutoMixHeadroomEnabled { get; set; } = true;
    public bool TrackSession { get; set; } = true;
    public string? RandomPoolShortcut { get; set; }
    public List<Guid> RandomPoolDeckIds { get; set; } = [];
    public List<Guid> RandomPoolJingleIds { get; set; } = [];
    public List<RandomPoolProfile> RandomPoolProfiles { get; set; } = [];
    public List<TeamDeckProfile> TeamDeckProfiles { get; set; } = [];
}

public sealed class AutoplayProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ny Autoplay-lista";
    public string? PlaylistPath { get; set; }
    public string? Shortcut { get; set; }
    public double VolumeDb { get; set; }
}

public sealed class RandomPoolProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ny slumpgrupp";
    public string? Shortcut { get; set; }
    public List<Guid> DeckIds { get; set; } = [];
    public List<Guid> JingleIds { get; set; } = [];
}

public sealed class TeamDeckProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nytt lag";
    public string? Shortcut { get; set; }
    public Guid? DefaultJingleId { get; set; }
    // Position relativt standardjingelns klippta start där spelarövergången börjar.
    // Null väljer automatiskt standardjingelns slut minus fade out-tiden.
    public double? TransitionAtSeconds { get; set; }
    public double DefaultFadeOutSeconds { get; set; } = 1.5;
    public double PlayerFadeInSeconds { get; set; } = 0.75;
    public List<TeamDeckPlayer> Players { get; set; } = [];
}

public sealed class TeamDeckPlayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public string Name { get; set; } = "Ny spelare";
    public Guid? JingleId { get; set; }
    // Null betyder att den valda jingelns vanliga startposition används.
    // Ett värde här är en Team Deck-specifik, absolut position i ljudfilen.
    public double? StartSecondsOverride { get; set; }
    public string ButtonColor { get; set; } = "#17304A";
    public string TextColor { get; set; } = "#F3F7FC";
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
    private double? _detectedBpm;
    private double? _beatConfidence;
    private string? _detectedKey;
    private string? _detectedScale;
    private string? _camelotCode;
    private double? _keyConfidence;
    private double? _musicalEnergy;
    private double? _suggestedTransitionSeconds;
    private double? _suggestedTransitionFadeSeconds;
    private long _musicAnalysisFileSize;
    private long _musicAnalysisFileWriteUtcTicks;
    private double _musicAnalysisStartSeconds;
    private double _musicAnalysisEndSeconds;
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
    public double? DetectedBpm { get => _detectedBpm; set => Set(ref _detectedBpm, value); }
    public double? BeatConfidence { get => _beatConfidence; set => Set(ref _beatConfidence, value); }
    public string? DetectedKey { get => _detectedKey; set => Set(ref _detectedKey, value); }
    public string? DetectedScale { get => _detectedScale; set => Set(ref _detectedScale, value); }
    public string? CamelotCode { get => _camelotCode; set => Set(ref _camelotCode, value); }
    public double? KeyConfidence { get => _keyConfidence; set => Set(ref _keyConfidence, value); }
    public double? MusicalEnergy { get => _musicalEnergy; set => Set(ref _musicalEnergy, value); }
    public double? SuggestedTransitionSeconds { get => _suggestedTransitionSeconds; set => Set(ref _suggestedTransitionSeconds, value); }
    public double? SuggestedTransitionFadeSeconds { get => _suggestedTransitionFadeSeconds; set => Set(ref _suggestedTransitionFadeSeconds, value); }
    public long MusicAnalysisFileSize { get => _musicAnalysisFileSize; set { if (Set(ref _musicAnalysisFileSize, value)) Raise(nameof(HasFreshMusicAnalysis)); } }
    public long MusicAnalysisFileWriteUtcTicks { get => _musicAnalysisFileWriteUtcTicks; set { if (Set(ref _musicAnalysisFileWriteUtcTicks, value)) Raise(nameof(HasFreshMusicAnalysis)); } }
    public double MusicAnalysisStartSeconds { get => _musicAnalysisStartSeconds; set { if (Set(ref _musicAnalysisStartSeconds, value)) Raise(nameof(HasFreshMusicAnalysis)); } }
    public double MusicAnalysisEndSeconds { get => _musicAnalysisEndSeconds; set { if (Set(ref _musicAnalysisEndSeconds, value)) Raise(nameof(HasFreshMusicAnalysis)); } }
    [JsonIgnore] public bool HasFreshLoudnessAnalysis
    {
        get
        {
            if (!HasAudio || IntegratedLufs is null || TruePeakDbtp is null) return false;
            try { var file = new FileInfo(FilePath); return file.Length == AnalysisFileSize && file.LastWriteTimeUtc.Ticks == AnalysisFileWriteUtcTicks; }
            catch { return false; }
        }
    }
    [JsonIgnore] public bool HasFreshMusicAnalysis
    {
        get
        {
            if (!HasAudio || DetectedBpm is null || MusicalEnergy is null || SuggestedTransitionSeconds is null) return false;
            try
            {
                var file = new FileInfo(FilePath);
                var expectedEnd = EndSeconds ?? DurationSeconds;
                return file.Length == MusicAnalysisFileSize &&
                       file.LastWriteTimeUtc.Ticks == MusicAnalysisFileWriteUtcTicks &&
                       Math.Abs(StartSeconds - MusicAnalysisStartSeconds) < .001 &&
                       Math.Abs(expectedEnd - MusicAnalysisEndSeconds) < .001;
            }
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
    private int _pageCount = 1;
    private int _activePage;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }
    public int Rows { get => _rows; set { if (_rows == value) return; _rows = value; Raise(); } }
    public int Columns { get => _columns; set { if (_columns == value) return; _columns = value; Raise(); } }
    public int PageCount
    {
        get => _pageCount;
        set
        {
            var normalized = Math.Max(1, value);
            if (_pageCount == normalized) return;
            _pageCount = normalized;
            if (_activePage >= _pageCount) _activePage = _pageCount - 1;
            Raise();
            Raise(nameof(ActivePage));
            Raise(nameof(PageNumbers));
            Raise(nameof(HasMultiplePages));
        }
    }
    public int ActivePage
    {
        get => _activePage;
        set
        {
            var normalized = Math.Clamp(value, 0, Math.Max(0, PageCount - 1));
            if (_activePage == normalized) return;
            _activePage = normalized;
            Raise();
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<int> PageNumbers => Enumerable.Range(1, PageCount).ToArray();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMultiplePages => PageCount > 1;
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
