using System.Collections.ObjectModel;
using System.Windows.Threading;
using FloorballDJ.Infrastructure;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ProjectService _projects;
    private readonly ProfilePreferencesService _profilePreferences;
    private readonly AudioEngine _audio;
    private readonly DispatcherTimer _timer;
    private FloorballProject _project = ProjectService.CreateDefault();
    private Deck? _selectedDeck;
    private PlaybackSnapshot _nowPlaying = new(null, "Redo för nästa jingle", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false);
    private string _status = "Redo";
    private bool _isFadingOutCurrent;
    private bool _useSecondaryOutput;
    private bool _queueLoopEnabled = true;
    private bool _queueShuffleEnabled;
    private bool _autoplayModeActive;
    private bool _isSpaceResumePending;
    private int _queuePlaybackIndex;
    private Guid? _activeQueueJingleId;
    private Jingle? _activeQueueItem;
    private bool _queueTransitionStarted;
    private Guid? _previewOnlyJingleId;
    private string? _currentPath;
    private CancellationTokenSource? _saveRequestCancellation;
    private bool _disposed;

    public MainViewModel(ProjectService projects, ProfilePreferencesService profilePreferences, AudioEngine audio)
    {
        _projects = projects;
        _profilePreferences = profilePreferences;
        _audio = audio;
        _selectedDeck = _project.Decks.FirstOrDefault();
        _audio.SnapshotChanged += (_, snapshot) =>
        {
            NowPlaying = snapshot;
            if (snapshot.JingleId is null && _isFadingOutCurrent)
            {
                _isFadingOutCurrent = false;
                Raise(nameof(NowPlayingLabel));
            }
        };
        _audio.PlaybackFailed += (_, message) =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Status = $"Ljuduppspelningen avbröts: {message}");
        _audio.PlaybackCompleted += (_, jingle) =>
        {
            if (_previewOnlyJingleId == jingle.Id) { _previewOnlyJingleId = null; return; }
            if (!AutoplayModeActive) return;
            if (!PlaybackQueue.Any(item => item.Id == jingle.Id || string.Equals(item.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))) return;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!PlayNextQueued()) ActiveQueueItem = null;
            });
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            _audio.PublishSnapshot();
            TryStartQueueTransition();
        };
        _timer.Start();
    }

    public FloorballProject Project { get => _project; private set { if (Set(ref _project, value)) RaiseAll(); } }
    public AppSettings Settings => Project.Settings;
    public ObservableCollection<Deck> Decks => Project.Decks;
    public Deck? SelectedDeck { get => _selectedDeck; set => Set(ref _selectedDeck, value); }
    public PlaybackSnapshot NowPlaying { get => _nowPlaying; private set { if (Set(ref _nowPlaying, value)) { Raise(nameof(RemainingText)); Raise(nameof(PositionFraction)); } } }
    public string RemainingText => NowPlaying.Duration <= TimeSpan.Zero ? "--:--.-" : Format(NowPlaying.Duration - NowPlaying.Position);
    public double PositionFraction => NowPlaying.Duration.TotalSeconds <= 0 ? 0 : Math.Clamp(NowPlaying.Position.TotalSeconds / NowPlaying.Duration.TotalSeconds, 0, 1);
    public string Status { get => _status; set => Set(ref _status, value); }
    public string NowPlayingLabel => _isFadingOutCurrent ? "FADEAS UT" : "SPELAR NU";
    public bool UseSecondaryOutput { get => _useSecondaryOutput; set => Set(ref _useSecondaryOutput, value); }
    public bool QueueLoopEnabled { get => _queueLoopEnabled; set => Set(ref _queueLoopEnabled, value); }
    public bool QueueShuffleEnabled { get => _queueShuffleEnabled; set => Set(ref _queueShuffleEnabled, value); }
    public bool AutoplayModeActive { get => _autoplayModeActive; private set => Set(ref _autoplayModeActive, value); }
    public bool IsSpaceResumePending { get => _isSpaceResumePending; set => Set(ref _isSpaceResumePending, value); }
    public Jingle? ActiveQueueItem { get => _activeQueueItem; private set => Set(ref _activeQueueItem, value); }
    public double QueueTransitionSeconds
    {
        get => Settings.AutoplayTransitionSeconds;
        set
        {
            var normalized = Math.Clamp(value, 0, 30);
            if (Math.Abs(Settings.AutoplayTransitionSeconds - normalized) < 0.001) return;
            Settings.AutoplayTransitionSeconds = normalized;
            Raise();
        }
    }
    public ObservableCollection<Jingle> PlaybackQueue { get; } = [];
    public ObservableCollection<Jingle> DeckPlaybackQueue { get; } = [];
    public bool HasDeckPlaybackQueue => DeckPlaybackQueue.Count > 0;
    public int QueuedCount => AutoplayModeActive ? PlaybackQueue.Count : DeckPlaybackQueue.Count;
    public AudioEngine Audio => _audio;
    public string CurrentProjectPath => _currentPath ?? _projects.DefaultProjectPath;

    public async Task InitializeAsync()
    {
        var defaultProfilePath = _profilePreferences.GetDefaultProfilePath();
        if (!string.IsNullOrWhiteSpace(defaultProfilePath) && File.Exists(defaultProfilePath))
        {
            try
            {
                await LoadAsync(defaultProfilePath);
                Status = $"Standardprofil öppnad: {GetProfileDisplayName(defaultProfilePath)}";
                ConfigureAudio();
                return;
            }
            catch { Status = "Standardprofilen kunde inte öppnas"; }
        }
        if (File.Exists(_projects.DefaultProjectPath))
        {
            try { await LoadAsync(_projects.DefaultProjectPath); Status = "Senaste projektet återställt"; }
            catch { Status = "Ett nytt projekt skapades"; }
        }
        ConfigureAudio();
    }

    public void Play(Jingle jingle)
    {
        _activeQueueJingleId = null;
        _queueTransitionStarted = false;
        ActiveQueueItem = null;
        var queued = DeckPlaybackQueue.FirstOrDefault(item => item.Id == jingle.Id);
        if (queued is not null)
        {
            DeckPlaybackQueue.Remove(queued);
            UpdateQueuePositions();
        }
        PlayCore(jingle, true, false);
    }

    public void PlayPreview(Jingle jingle)
        => PlayCore(jingle, true, true);

    private bool PlayCore(Jingle jingle, bool honorJingleLoop, bool previewOnly, double? transitionFadeInSeconds = null, double? transitionFadeOutSeconds = null)
    {
        _previewOnlyJingleId = previewOnly ? jingle.Id : null;
        PlaybackAction action;
        try
        {
            action = _audio.Play(jingle, honorJingleLoop, transitionFadeInSeconds, transitionFadeOutSeconds);
        }
        catch (Exception ex)
        {
            if (_previewOnlyJingleId == jingle.Id) _previewOnlyJingleId = null;
            Status = $"Kunde inte spela {jingle.Title}: {ex.Message}";
            return false;
        }

        if (action == PlaybackAction.Started)
        {
            _isFadingOutCurrent = false;
            Raise(nameof(NowPlayingLabel));
            if (Settings.TrackSession) jingle.SessionPlayCount++;
            Status = $"Spelar: {jingle.Title}";
        }
        else if (action == PlaybackAction.FadingOut)
        {
            _isFadingOutCurrent = true;
            Raise(nameof(NowPlayingLabel));
            Status = $"Tonar ut: {jingle.Title}";
        }
        else
        {
            Status = $"{jingle.Title}: maximalt 8 samtidiga instanser";
            return false;
        }
        return true;
    }

    public void ConfigureAudio() => _audio.Configure(Settings.OutputDeviceId, Settings.SecondaryOutputDeviceId, Settings.MasterVolumeDb,
        Settings.DuckLevelDb, Settings.FadeInSeconds, Settings.FadeOutSeconds,
        Settings.MasterLimiterEnabled, Settings.MasterLimiterCeilingDbtp, Settings.AutoMixHeadroomEnabled,
        Settings.TalkDuckLevelDb);
    public void SetSecondaryOutput(bool enabled)
    {
        UseSecondaryOutput = enabled;
        _audio.SetSecondaryOutput(enabled);
        Status = enabled ? "Förlyssning via utgång 2" : "Huvudutgång aktiv";
    }

    public void SetAutoplayMode(bool active)
    {
        AutoplayModeActive = active;
        Raise(nameof(QueuedCount));
        if (active) return;
        _activeQueueJingleId = null;
        _queueTransitionStarted = false;
        ActiveQueueItem = null;
    }

    public void ToggleQueue(Jingle jingle)
    {
        var existing = DeckPlaybackQueue.FirstOrDefault(item => item.Id == jingle.Id);
        if (existing is not null) DeckPlaybackQueue.Remove(existing); else DeckPlaybackQueue.Add(jingle);
        UpdateQueuePositions();
        Status = existing is null ? $"Köade {jingle.Title}" : $"Tog bort {jingle.Title} från kön";
    }

    public bool PlayNextDeckQueued()
    {
        while (DeckPlaybackQueue.Count > 0)
        {
            var next = DeckPlaybackQueue[0];
            DeckPlaybackQueue.RemoveAt(0);
            UpdateQueuePositions();
            _activeQueueJingleId = null;
            _queueTransitionStarted = false;
            ActiveQueueItem = null;
            if (PlayCore(next, true, false)) return true;
        }
        return false;
    }

    public void ClearDeckQueue()
    {
        DeckPlaybackQueue.Clear();
        UpdateQueuePositions();
    }

    public void AddToQueue(Jingle jingle)
    {
        if (PlaybackQueue.Any(item => item.Id == jingle.Id && string.Equals(item.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))) return;
        PlaybackQueue.Add(jingle);
        _queuePlaybackIndex = 0;
        UpdateQueuePositions();
    }

    public void RemoveFromQueue(Jingle jingle)
    {
        var existing = PlaybackQueue.FirstOrDefault(item => item.Id == jingle.Id ||
            string.Equals(item.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) PlaybackQueue.Remove(existing);
        _queuePlaybackIndex = 0;
        UpdateQueuePositions();
    }

    public bool PlayNextQueued(bool crossfade = false)
    {
        if (PlaybackQueue.Count == 0) return false;
        for (var attempt = 0; attempt < PlaybackQueue.Count; attempt++)
        {
            if (_queuePlaybackIndex >= PlaybackQueue.Count)
            {
                if (!QueueLoopEnabled) return false;
                _queuePlaybackIndex = 0;
            }
            var next = QueueShuffleEnabled ? PlaybackQueue[Random.Shared.Next(PlaybackQueue.Count)] : PlaybackQueue[_queuePlaybackIndex];
            _queuePlaybackIndex++;
            _activeQueueJingleId = next.Id;
            ActiveQueueItem = next;
            _queueTransitionStarted = false;
            if (PlayCore(next, false, false,
                    crossfade ? QueueTransitionSeconds * 0.25d : null,
                    crossfade ? QueueTransitionSeconds * 0.75d : null))
                return true;
        }
        _activeQueueJingleId = null;
        ActiveQueueItem = null;
        return false;
    }

    public void PlayQueuedItem(Jingle item)
    {
        var index = PlaybackQueue.IndexOf(item);
        if (index >= 0) _queuePlaybackIndex = index + 1;
        _activeQueueJingleId = item.Id;
        ActiveQueueItem = item;
        _queueTransitionStarted = false;
        if (!PlayCore(item, false, false))
        {
            _activeQueueJingleId = null;
            ActiveQueueItem = null;
        }
    }

    private void TryStartQueueTransition()
    {
        if (!AutoplayModeActive || _queueTransitionStarted || PlaybackQueue.Count < 2 || _activeQueueJingleId is null || NowPlaying.JingleId != _activeQueueJingleId || _previewOnlyJingleId is not null) return;
        var fadeOutSeconds = QueueTransitionSeconds * 0.75d;
        if (fadeOutSeconds <= 0 || NowPlaying.Duration <= TimeSpan.Zero || NowPlaying.Position <= TimeSpan.Zero) return;
        if ((NowPlaying.Duration - NowPlaying.Position).TotalSeconds > fadeOutSeconds) return;
        _queueTransitionStarted = true;
        PlayNextQueued(true);
    }

    public void ReplaceQueue(IEnumerable<Jingle> items)
    {
        foreach (var item in PlaybackQueue) item.AutoplayQueuePosition = 0;
        PlaybackQueue.Clear();
        foreach (var item in items) PlaybackQueue.Add(item);
        _queuePlaybackIndex = 0;
        _activeQueueJingleId = null;
        ActiveQueueItem = null;
        _queueTransitionStarted = false;
        UpdateQueuePositions();
    }

    public void MoveQueueItem(Jingle item, int offset)
    {
        var oldIndex = PlaybackQueue.IndexOf(item);
        var newIndex = Math.Clamp(oldIndex + offset, 0, PlaybackQueue.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        PlaybackQueue.Move(oldIndex, newIndex);
        _queuePlaybackIndex = 0;
        UpdateQueuePositions();
    }

    public void MarkFadingOut()
    {
        if (NowPlaying.JingleId is null) return;
        _isFadingOutCurrent = true;
        Raise(nameof(NowPlayingLabel));
        Status = $"Tonar ut: {NowPlaying.Title}";
    }

    private void UpdateQueuePositions()
    {
        foreach (var jingle in Decks.SelectMany(deck => deck.Jingles)) jingle.QueuePosition = 0;
        for (var index = 0; index < DeckPlaybackQueue.Count; index++) DeckPlaybackQueue[index].QueuePosition = index + 1;
        foreach (var item in PlaybackQueue) item.AutoplayQueuePosition = 0;
        for (var index = 0; index < PlaybackQueue.Count; index++) PlaybackQueue[index].AutoplayQueuePosition = index + 1;
        Raise(nameof(HasDeckPlaybackQueue));
        Raise(nameof(QueuedCount));
    }

    public void MoveDeck(Deck source, Deck target)
    {
        var sourceIndex = Decks.IndexOf(source);
        var targetIndex = Decks.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;
        Decks.Move(sourceIndex, targetIndex);
        SelectedDeck = source;
        Status = $"Flyttade {source.Name} till plats {targetIndex + 1}";
        RequestSave();
    }
    public void ApplyLayout()
    {
        ProjectService.EnsureLayout(Project);
        SelectedDeck ??= Decks.FirstOrDefault();
        RaiseAll();
    }

    public async Task SaveAsync(string? path = null)
    {
        _currentPath = path ?? _currentPath ?? _projects.DefaultProjectPath;
        await _projects.SaveAsync(Project, _currentPath);
        TrackProfile(_currentPath);
        Status = $"Sparat {DateTime.Now:HH:mm:ss}";
    }

    public void RequestSave()
    {
        if (_disposed) return;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _saveRequestCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = SaveAfterDelayAsync(cancellation.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(175, cancellationToken);
            await SaveAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"Autosparning misslyckades: {ex.Message}"; }
    }

    public async Task LoadAsync(string path)
    {
        ClearDeckQueue();
        Project = await _projects.LoadAsync(path);
        ProjectService.EnsureLayout(Project);
        SelectedDeck = Decks.FirstOrDefault();
        _currentPath = path;
        TrackProfile(path);
        ConfigureAudio();
        Status = $"Öppnade {Path.GetFileName(path)}";
    }

    private void TrackProfile(string path)
    {
        try { _profilePreferences.RecordProfile(path); }
        catch (Exception ex) { Status = $"Profilen öppnades, men historiken kunde inte sparas: {ex.Message}"; }
    }

    private static string GetProfileDisplayName(string path)
    {
        var fileName = Path.GetFileName(path);
        const string profileSuffix = ".floorballdj.json";
        return fileName.EndsWith(profileSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^profileSuffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    public Task<IReadOnlyList<ProjectRevision>> GetRevisionsAsync()
        => _projects.GetRevisionsAsync(CurrentProjectPath);

    public async Task RestoreRevisionAsync(string revisionPath)
    {
        var targetPath = CurrentProjectPath;
        ClearDeckQueue();
        Project = await _projects.LoadAsync(revisionPath);
        ProjectService.EnsureLayout(Project);
        SelectedDeck = Decks.FirstOrDefault();
        _currentPath = targetPath;
        ReplaceQueue([]);
        ConfigureAudio();
        await SaveAsync(targetPath);
        Status = $"Återställde revision från {DateTime.Now:HH:mm:ss}";
    }

    public void ImportLegacyXml(string path)
    {
        ClearDeckQueue();
        Project = LegacyXmlImporter.Import(path);
        SelectedDeck = Decks.FirstOrDefault();
        _currentPath = null;
        ConfigureAudio();
        Status = "Äldre XML importerad";
    }

    public async Task<string> BackupAsync() => await _projects.BackupAsync(Project);
    public void NotifyJingleChanged(Jingle? jingle = null)
    {
        if (jingle is not null)
        {
            var deck = Decks.FirstOrDefault(candidate => candidate.Jingles.Contains(jingle));
            var index = deck?.Jingles.IndexOf(jingle) ?? -1;
            if (deck is not null && index >= 0) deck.Jingles[index] = jingle;
        }
        Raise(nameof(Project));
        Raise(nameof(SelectedDeck));
        _audio.RefreshVolumes();
    }
    public static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss\.f");
    private void RaiseAll() { Raise(nameof(Settings)); Raise(nameof(Decks)); Raise(nameof(SelectedDeck)); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _saveRequestCancellation?.Cancel();
        _saveRequestCancellation?.Dispose();
        _audio.Dispose();
    }
}
