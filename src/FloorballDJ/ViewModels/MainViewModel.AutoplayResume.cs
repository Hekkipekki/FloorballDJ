using FloorballDJ.Models;

namespace FloorballDJ.ViewModels;

public sealed partial class MainViewModel
{
    internal int? LoadedPlaylistIndex(string path)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        foreach (var slot in new[] { _playingQueueIndex, 1 - _playingQueueIndex })
        {
            var queue = _autoplayQueues[slot];
            if (queue.Items.Count == 0 || string.IsNullOrWhiteSpace(queue.Path)) continue;
            if (string.Equals(Path.GetFullPath(queue.Path), normalized, StringComparison.OrdinalIgnoreCase)) return slot;
        }
        return null;
    }

    private void RememberCurrentQueuePosition() => RememberQueuePosition(NowPlaying);
    private void RememberQueuePosition(PlaybackSnapshot snapshot)
    {
        var queue = _autoplayQueues[_playingQueueIndex];
        if (_activeQueueJingleId is null || snapshot.JingleId != _activeQueueJingleId || queue.LastItem?.Id != snapshot.JingleId) return;
        queue.ResumePosition = snapshot.Position;
        queue.LastDuration = snapshot.Duration;
    }

    internal async Task<bool> ResumeLoadedPlaylistAsync(int slot, double volumeDb)
    {
        EditingQueueIndex = slot;
        _audio.PublishSnapshot();
        CancelAutoplayTransition();
        var queue = _autoplayQueues[slot];
        var last = queue.LastItem;
        if (_playingQueueIndex == slot && last is not null && _activeQueueJingleId == last.Id && NowPlaying.JingleId == last.Id)
        {
            if (NowPlaying.IsPaused)
            {
                await _audio.PauseOrResumeAsync();
                return true;
            }
            if (NowPlaying.IsPlaying && !NowPlaying.IsFadingOut) return true;
            if (NowPlaying.IsFadingOut && _audio.TryResumeSpaceFade()) return true;
        }
        SetPlayingQueue(slot);
        queue.GainDb = Math.Clamp(volumeDb, -60, 12);
        if (last is not null && queue.Items.Contains(last) &&
            (queue.LastDuration <= TimeSpan.Zero || queue.ResumePosition < queue.LastDuration))
        {
            Resume(last, queue.ResumePosition, queuedItem: true);
            return ActiveQueueItem is not null;
        }
        return PlayNextFromQueue(slot, false);
    }
}
