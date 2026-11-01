using System.Collections.ObjectModel;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.ViewModels;

public sealed partial class MainViewModel
{
    private sealed class AutoplayQueue
    {
        public ObservableCollection<Jingle> Items { get; } = [];
        public int Cursor;
        public int ShuffleProgress;
        public Jingle? LastItem;
        public TimeSpan ResumePosition;
        public TimeSpan LastDuration;
        public bool Loop = true;
        public bool Shuffle;
        public double GainDb;
        public string? Path;
    }

    private readonly AutoplayQueue[] _autoplayQueues = [new(), new()];
    private int _editingQueueIndex;
    private int _playingQueueIndex;
    private int? _queueContextIndex;
    private int QueueContextIndex => _queueContextIndex ?? _editingQueueIndex;
    private AutoplayQueue QueueState => _autoplayQueues[QueueContextIndex];
    public ObservableCollection<Jingle> LeftPlaybackQueue => _autoplayQueues[0].Items;
    public ObservableCollection<Jingle> RightPlaybackQueue => _autoplayQueues[1].Items;
    public ObservableCollection<Jingle> PlayingPlaybackQueue => _autoplayQueues[_playingQueueIndex].Items;
    internal int AutoplayTransportQueueIndex => _activeQueueJingleId is null ? _editingQueueIndex : _playingQueueIndex;
    public ObservableCollection<Jingle> AutoplayTransportQueue => _autoplayQueues[AutoplayTransportQueueIndex].Items;
    public bool IsAutoplayQueued(Jingle item) => _autoplayQueues[_playingQueueIndex].Items.Contains(item);
    internal int ShortcutQueueIndex()
    {
        var playing = _activeQueueJingleId is not null;
        for (var slot = 0; slot < 2; slot++)
            if (_autoplayQueues[slot].Items.Count == 0) return slot;
        return playing ? 1 - _playingQueueIndex : _editingQueueIndex;
    }
    public int EditingQueueIndex
    {
        get => _editingQueueIndex;
        set
        {
            if (value is < 0 or > 1 || !Set(ref _editingQueueIndex, value)) return;
            RefreshQueueBindings();
            UpdateQueuePositions();
        }
    }

    public string LeftPlaylistTitle => PlaylistTitle(0);
    public string RightPlaylistTitle => PlaylistTitle(1);
    public string EditingPlaylistTitle => PlaylistTitle(_editingQueueIndex);
    public string PlaybackSourceLabel => _activeQueueJingleId is null ? LanguageService.Translate("Ingen aktiv spellista") :
        string.Format(LanguageService.Translate("Spelar från: {0}"), PlaylistTitle(_playingQueueIndex));
    public string? QueuePlaylistPath
    {
        get => QueueState.Path;
        set { QueueState.Path = value; PersistAutoplayQueues(); RefreshQueueBindings(); }
    }
    private string PlaylistTitle(int index) => string.IsNullOrEmpty(_autoplayQueues[index].Path)
        ? string.Format(LanguageService.Translate("Spellista {0}"), index + 1) : System.IO.Path.GetFileName(_autoplayQueues[index].Path)!.Replace(".fdjplaylist.json", "", StringComparison.OrdinalIgnoreCase);

    // This scope is synchronous: asynchronous preparation captures its destination
    // and re-enters only when applying the completed result on the UI thread.
    internal void InQueue(int index, Action action) => InQueue(index, () => { action(); return true; });
    internal T InQueue<T>(int index, Func<T> action)
    {
        var previous = _queueContextIndex;
        _queueContextIndex = index;
        try { return action(); }
        finally { _queueContextIndex = previous; }
    }

    public bool PlayNextFromQueue(int index, bool crossfade = true)
    {
        if (_autoplayQueues[index].Items.Count == 0) return false;
        SetPlayingQueue(index);
        return PlayNextQueued(crossfade);
    }

    public bool PlayNextAutoplay(bool crossfade = true) => _activeQueueJingleId is null
        ? PlayNextFromQueue(_editingQueueIndex, crossfade) : PlayNextQueued(crossfade);

    private void SetPlayingQueue(int index)
    {
        RememberCurrentQueuePosition();
        if (index != _playingQueueIndex) CancelAutoplayTransition();
        _playingQueueIndex = index;
        Raise(nameof(PlayingPlaybackQueue));
        Raise(nameof(QueuedCount));
        Raise(nameof(PlaybackSourceLabel));
    }

    private void InitializeAutoplayQueues()
    {
        for (var index = 0; index < 2; index++)
        {
            var slot = index;
            _autoplayQueues[index].Items.CollectionChanged += (_, change) => InQueue(slot, () =>
            {
                QueueRevision++;
                if (slot == _playingQueueIndex) CancelQueuePrefetch();
                if (change.OldItems is not null)
                    foreach (Jingle item in change.OldItems) item.AutoplayQueuePosition = 0;
                if (!_restoringQueue)
                {
                    RebaseQueueCursor(change);
                    PersistAutoplayQueues();
                }
            });
        }
    }

    private void PersistAutoplayQueues()
    {
        if (_restoringQueue) return;
        Settings.AutoplayQueueItems = LeftPlaybackQueue.ToList();
        Settings.AutoplaySecondQueueItems = RightPlaybackQueue.ToList();
        Settings.AutoplayQueueLoopEnabled = _autoplayQueues[0].Loop;
        Settings.AutoplayQueueShuffleEnabled = _autoplayQueues[0].Shuffle;
        Settings.AutoplaySecondQueueLoopEnabled = _autoplayQueues[1].Loop;
        Settings.AutoplaySecondQueueShuffleEnabled = _autoplayQueues[1].Shuffle;
        Settings.AutoplayLastPlaylistPath = _autoplayQueues[0].Path;
        Settings.AutoplaySecondPlaylistPath = _autoplayQueues[1].Path;
        RequestSave();
    }

    private void RefreshQueueBindings() => InQueue(_editingQueueIndex, () =>
    {
        Raise(nameof(PlaybackQueue));
        Raise(nameof(QueueClipOptionsRevision));
        Raise(nameof(QueueTransitionTypeIndex));
        Raise(nameof(IsQueueCrossfade));
        Raise(nameof(IsQueueFadeInOut));
        Raise(nameof(QueueTransitionSeconds));
        Raise(nameof(QueueFadeInSeconds));
        Raise(nameof(QueueFadeOutSeconds));
        Raise(nameof(QueueLoopEnabled));
        Raise(nameof(QueueShuffleEnabled));
        Raise(nameof(QueuePlaylistPath));
        Raise(nameof(LeftPlaylistTitle));
        Raise(nameof(RightPlaylistTitle));
        Raise(nameof(EditingPlaylistTitle));
        Raise(nameof(PlaybackSourceLabel));
        Raise(nameof(QueuedCount));
    });
}
