using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.ViewModels;

public sealed partial class MainViewModel
{
    public long QueueClipOptionsRevision { get; private set; }
    private List<AutoplayClipOptions> ClipOptions(int slot) => slot == 0
        ? Settings.AutoplayClipOptions ??= [] : Settings.AutoplaySecondClipOptions ??= [];
    public AutoplayClipOptions GetQueueClipOptions(int slot, Jingle song) =>
        ClipOptions(slot).FirstOrDefault(item => item.JingleId == song.Id) ?? new(song.Id);
    private PlaybackClip QueueClip(Jingle song)
    {
        var options = GetQueueClipOptions(QueueContextIndex, song);
        return new(options.UseRawFile ? 0 : song.StartSeconds,
            options.EndSeconds ?? (options.UseRawFile ? null : song.EndSeconds));
    }

    public void SetQueueClipOptions(int slot, Jingle song, bool rawFile, double? endSeconds, double? fileDurationSeconds = null)
    {
        var start = rawFile ? 0 : song.StartSeconds;
        var previous = GetQueueClipOptions(slot, song);
        var duration = fileDurationSeconds ?? song.DurationSeconds;
        if (endSeconds is { } end && (!double.IsFinite(end) || end <= start ||
            (duration > 0 && end > duration && endSeconds != previous.EndSeconds)))
            throw new ArgumentOutOfRangeException(nameof(endSeconds));
        var options = ClipOptions(slot);
        options.RemoveAll(item => item.JingleId == song.Id);
        if (rawFile || endSeconds.HasValue) options.Add(new(song.Id, rawFile, endSeconds));
        QueueRevision++;
        QueueClipOptionsRevision++;
        Raise(nameof(QueueClipOptionsRevision));
        RequestSave();
        // Changing the end of the playing song takes effect without a restart.
        // Switching between raw/deck start positions takes effect on the next start.
        if (previous.UseRawFile == rawFile && slot == _playingQueueIndex && _activeQueueJingleId == song.Id && ActiveQueueItem is not null)
        {
            var clip = InQueue(slot, () => QueueClip(song));
            _audio.UpdatePrimaryClipEnd(song.Id, clip.EndSeconds);
        }
    }

    public void PlayQueuePreview(Jingle song)
    {
        PlayCore(song, true, true, clipOverride: QueueClip(song));
        PreviewPlaying = _audio.GetSecondarySnapshot();
    }

    public int QueueTransitionTypeIndex
    {
        get => (int)(QueueContextIndex == 0 ? Settings.AutoplayTransitionType : Settings.AutoplaySecondTransitionType);
        set
        {
            if (value is < 0 or > 1 || QueueTransitionTypeIndex == value) return;
            if (QueueContextIndex == 0) Settings.AutoplayTransitionType = (AutoplayTransitionType)value;
            else Settings.AutoplaySecondTransitionType = (AutoplayTransitionType)value;
            Raise(); Raise(nameof(IsQueueCrossfade)); Raise(nameof(IsQueueFadeInOut)); RequestSave();
        }
    }
    public bool IsQueueCrossfade => QueueTransitionTypeIndex == 0;
    public bool IsQueueFadeInOut => !IsQueueCrossfade;
    public double QueueFadeInSeconds
    {
        get => QueueContextIndex == 0 ? Settings.AutoplayFadeInSeconds : Settings.AutoplaySecondFadeInSeconds;
        set { var seconds = TransitionSeconds(value); if (QueueContextIndex == 0) Settings.AutoplayFadeInSeconds = seconds; else Settings.AutoplaySecondFadeInSeconds = seconds; Raise(); RequestSave(); }
    }
    public double QueueFadeOutSeconds
    {
        get => QueueContextIndex == 0 ? Settings.AutoplayFadeOutSeconds : Settings.AutoplaySecondFadeOutSeconds;
        set { var seconds = TransitionSeconds(value); if (QueueContextIndex == 0) Settings.AutoplayFadeOutSeconds = seconds; else Settings.AutoplaySecondFadeOutSeconds = seconds; Raise(); RequestSave(); }
    }
    private static double TransitionSeconds(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 30) : 0;

    private long _autoplayTransitionVersion;
    private bool _sequentialTransitionPending;
    internal void CancelAutoplayTransition()
    {
        RememberCurrentQueuePosition();
        _autoplayTransitionVersion++;
        _sequentialTransitionPending = false;
        _queueTransitionStarted = false;
    }
    private bool BeginSequentialTransition()
    {
        if (_sequentialTransitionPending) return true;
        if (NextQueuePrefetchPath() is null) return false;
        _sequentialTransitionPending = true;
        _queueTransitionStarted = true;
        var version = ++_autoplayTransitionVersion;
        var slot = _playingQueueIndex;
        var project = Project;
        var active = _activeQueueJingleId;
        _ = CompleteSequentialTransitionAsync(version, slot, project, active, QueueFadeOutSeconds);
        return true;
    }
    private async Task CompleteSequentialTransitionAsync(long version, int slot, FloorballProject project, Guid? active, double seconds)
    {
        try
        {
            await _audio.FadeOutPrimaryOutputAsync(seconds);
            if (version != _autoplayTransitionVersion || !ReferenceEquals(Project, project) || !AutoplayModeActive || _activeQueueJingleId != active) return;
            _sequentialTransitionPending = false;
            InQueue(slot, () => { if (!PlayNextQueuedCore(false)) ActiveQueueItem = null; });
        }
        catch (Exception ex)
        {
            if (version == _autoplayTransitionVersion) { _sequentialTransitionPending = false; _queueTransitionStarted = false; Status = $"Kunde inte byta låt: {ex.Message}"; }
        }
    }
}
