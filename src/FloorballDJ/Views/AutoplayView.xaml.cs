using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using Microsoft.Win32;

namespace FloorballDJ.Views;

public partial class AutoplayView : UserControl
{
    public event EventHandler? SongPickerRequested;
    private void ChooseDeckSongs_Click(object sender, RoutedEventArgs e) => SongPickerRequested?.Invoke(this, EventArgs.Empty);
    private readonly List<Jingle> _folderItems = [];
    private readonly SemaphoreSlim _libraryGate = new(1, 1);
    private Point _queueDragStart;
    private Jingle? _queueDragItem;
    private int _queueDragSlot;
    private ListBoxItem? _queueDropTarget;
    private bool _dropAfter;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _playlistCancellation;
    private long _playlistVersion;
    private FloorballProject? _folderProject;
    private string? _folderPath;
    internal PlaylistService Playlists { get; init; } = PlaylistService.Shared;
    internal Func<string, HashSet<string>, CancellationToken, List<string>> LibraryEnumerator { get; init; } = EnumerateAudioFiles;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public AutoplayView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            CancelPlaylistPreparation();
            _refreshCancellation?.Cancel();
            if (e.OldValue is MainViewModel old)
            {
                old.PrimaryPlaybackStarted -= PrimaryPlaybackStarted;
                old.PropertyChanged -= ViewModelChanged;
            }
            if (e.NewValue is MainViewModel current)
            {
                current.PrimaryPlaybackStarted += PrimaryPlaybackStarted;
                current.PropertyChanged += ViewModelChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            _playlistVersion++;
            CancelPlaylistPreparation();
            _refreshCancellation?.Cancel();
        };
    }

    private void PrimaryPlaybackStarted(object? sender, EventArgs e) => CancelPlaylistPreparation();
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EditingQueueIndex)) CancelPlaylistPreparation();
        if (e.PropertyName == nameof(MainViewModel.ActiveQueueItem) && sender is MainViewModel current && current.ActiveQueueItem is { } active)
        {
            var list = ReferenceEquals(current.PlayingPlaybackQueue, current.RightPlaybackQueue) ? RightQueueList : QueueList;
            list.SelectedItem = active;
            list.Dispatcher.BeginInvoke(() => { if (list.Items.Contains(active)) list.ScrollIntoView(active); });
        }
        if (e.PropertyName == nameof(MainViewModel.AutoplayModeActive) && sender is MainViewModel { AutoplayModeActive: false })
            CancelPlaylistPreparation();
    }
    internal void CancelPlaylistPreparation()
    {
        if (_playlistCancellation is { } pending)
        {
            _playlistVersion++;
            pending.Cancel();
        }
        if (DataContext is MainViewModel viewModel) viewModel.CancelQueuePrefetch();
    }

    public async Task RefreshAvailableAsync()
    {
        if (DataContext is not MainViewModel viewModel) return;
        var project = viewModel.Project;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(viewModel.ProfileWorkCancellation);
        var token = cancellation.Token;
        var previousCancellation = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previousCancellation?.Cancel();
        var folder = viewModel.Settings.MusicFolderPath;
        var deckPaths = viewModel.Decks.SelectMany(deck => deck.Jingles).Select(jingle => jingle.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            _folderItems.Clear();
            _folderProject = null;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    await _libraryGate.WaitAsync(token);
                    List<string> paths;
                    try
                    {
                        paths = await Task.Run(() =>
                        {
                            var performance = PerformanceDiagnostics.BeginOperation("AutoplayLibraryRequested");
                            using var duration = performance.Measure("AutoplayLibraryScan");
                            return Directory.Exists(folder) ? LibraryEnumerator(folder, deckPaths, token) : [];
                        }, token);
                    }
                    finally { _libraryGate.Release(); }
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(DataContext, viewModel) || !ReferenceEquals(project, viewModel.Project) || folder != viewModel.Settings.MusicFolderPath) return;
                    foreach (var path in paths)
                        _folderItems.Add(new Jingle { Title = Path.GetFileNameWithoutExtension(path), FilePath = path, PlayMode = JinglePlayMode.Solo });
                }
                catch (OperationCanceledException) { return; }
                catch { }
            }
            token.ThrowIfCancellationRequested();
            _folderProject = project;
            _folderPath = folder;
            FolderText.Text = string.IsNullOrWhiteSpace(folder) ? "Ingen musikmapp vald" : $"Musikmapp: {new DirectoryInfo(folder).Name}";
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static Jingle RawFile(string path) => new()
    {
        Title = Path.GetFileNameWithoutExtension(path), FilePath = path, StartSeconds = 0, EndSeconds = null,
        DurationSeconds = 0, GainDb = 0, PitchSemitones = 0, TempoPercent = 0, RatePercent = 0,
        FadeInOverrideSeconds = null, FadeOutOverrideSeconds = null, PlayMode = JinglePlayMode.Solo
    };

    private async void ChooseMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Välj mappen där musiken sparas", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await AddMusicFolderAsync(dialog.FolderName);
    }

    internal async Task AddMusicFolderAsync(string folder)
    {
        var vm = ViewModel;
        var project = vm.Project;
        var slot = vm.EditingQueueIndex;
        var revision = vm.QueueRevision;
        CancelPlaylistPreparation();
        vm.Settings.MusicFolderPath = folder;
        vm.RequestSave();
        await RefreshAvailableAsync();
        if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(project, vm.Project) ||
            !ReferenceEquals(_folderProject, project) || _folderPath != folder || slot != vm.EditingQueueIndex || revision != vm.QueueRevision) return;
        var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var items = vm.Decks.SelectMany(deck => deck.Jingles)
            .Where(item => item.HasAudio && !item.IsTextBlock && Path.GetFullPath(item.FilePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(_folderItems).DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Title).ToArray();
        vm.InQueue(slot, () => { foreach (var item in items) vm.AddToQueue(item); });
        try { await vm.FlushSavesAsync(); } catch { }
    }

    private ListBox SelectedQueueList => ViewModel.EditingQueueIndex == 0 ? QueueList : RightQueueList;
    internal IReadOnlyList<Jingle> FolderItems => _folderItems;
    private void PlaylistPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SelectPlaylistPanel(sender);
    private void PlaylistPanel_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SelectPlaylistPanel(sender);
    private void SelectPlaylistPanel(object sender)
    {
        if (DataContext is MainViewModel vm && sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index)) vm.EditingQueueIndex = index;
    }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();
    private void RemoveSelected()
    {
        var list = SelectedQueueList;
        var index = list.SelectedIndex;
        if (list.SelectedItem is not Jingle item) return;
        ViewModel.RemoveFromQueue(item);
        list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
        if (list.SelectedItem is { } next) list.ScrollIntoView(next);
    }
    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected_Click(sender, e);
    private void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        ViewModel.EditingQueueIndex = ReferenceEquals(sender, RightQueueList) ? 1 : 0;
        RemoveSelected();
        e.Handled = true;
    }
    private void PlaySelected_Click(object sender, RoutedEventArgs e) { CancelPlaylistPreparation(); if (SelectedQueueList.SelectedItem is Jingle item) ViewModel.PlayQueuedItem(item); }
    private void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        CancelPlaylistPreparation();
        ViewModel.PlayNextAutoplay(crossfade: true);
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) { if (SelectedQueueList.SelectedItem is Jingle item) { ViewModel.MoveQueueItem(item, -1); SelectedQueueList.SelectedItem = item; } }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { if (SelectedQueueList.SelectedItem is Jingle item) { ViewModel.MoveQueueItem(item, 1); SelectedQueueList.SelectedItem = item; } }
    private void TransitionSecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var text = TransitionSecondsBox.Text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            viewModel.QueueTransitionSeconds = value;
    }

    private void TransitionSecondsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        TransitionSecondsBox.Text = ViewModel.QueueTransitionSeconds.ToString("0.#", CultureInfo.CurrentCulture);

    private void PreviewToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var enabled = PreviewToggle.IsChecked == true;
        if (enabled && string.IsNullOrWhiteSpace(viewModel.Settings.SecondaryOutputDeviceId))
        {
            MessageBox.Show(Window.GetWindow(this), "Välj först Utgångsenhet 2 under Verktyg > Inställningar.", "Ingen förlyssningsutgång vald", MessageBoxButton.OK, MessageBoxImage.Information);
            PreviewToggle.IsChecked = false;
            return;
        }
        if (!enabled && viewModel.UseSecondaryOutput) viewModel.Audio.StopSecondaryOutput();
        viewModel.SetSecondaryOutput(enabled);
    }

    private void PreviewPositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.ActualWidth <= 0 || !ViewModel.HasPreview) return;
        ViewModel.SeekPreview(e.GetPosition(slider).X / slider.ActualWidth);
        e.Handled = true;
    }

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Jingle item) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ViewModel.UseSecondaryOutput)
        {
            ViewModel.PlayQueuePreview(item);
            e.Handled = true;
            return;
        }
        ViewModel.EditingQueueIndex = ReferenceEquals(sender, RightQueueList) ? 1 : 0;
        _queueDragSlot = ViewModel.EditingQueueIndex;
        _queueDragStart = e.GetPosition(this);
        _queueDragItem = item;
    }

    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _queueDragItem is null) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _queueDragItem;
        _queueDragItem = null;
        var sourceContainer = SelectedQueueList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        if (sourceContainer is not null) sourceContainer.Opacity = 0.42;
        try { DragDrop.DoDragDrop(SelectedQueueList, new DataObject(typeof(Jingle), item), DragDropEffects.Move); }
        finally
        {
            if (sourceContainer is not null) sourceContainer.Opacity = 1;
            ClearQueueDropTarget();
        }
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (ReferenceEquals(sender, RightQueueList) ? 1 : 0) == _queueDragSlot && e.Data.GetDataPresent(typeof(Jingle)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move)
        {
            var container = ItemsControl.ContainerFromElement(SelectedQueueList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container is not null)
            {
                var position = e.GetPosition(container);
                SetQueueDropTarget(container, position.Y > container.ActualHeight / 2);
            }
            else ClearQueueDropTarget();
        }
        e.Handled = true;
    }

    private void QueueList_DragLeave(object sender, DragEventArgs e)
    {
        if (!SelectedQueueList.IsMouseOver) ClearQueueDropTarget();
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if ((ReferenceEquals(sender, RightQueueList) ? 1 : 0) != _queueDragSlot || e.Data.GetData(typeof(Jingle)) is not Jingle source) return;
        var oldIndex = ViewModel.PlaybackQueue.IndexOf(source);
        if (oldIndex >= 0)
        {
            var remaining = ViewModel.PlaybackQueue.Where(item => !ReferenceEquals(item, source)).ToList();
            var target = _queueDropTarget?.DataContext as Jingle;
            var newIndex = target is null ? remaining.Count : remaining.IndexOf(target) + (_dropAfter ? 1 : 0);
            newIndex = Math.Clamp(newIndex, 0, Math.Max(0, ViewModel.PlaybackQueue.Count - 1));
            ViewModel.MoveQueueItem(source, newIndex - oldIndex);
        }
        ClearQueueDropTarget();
        e.Handled = true;
    }

    private void SetQueueDropTarget(ListBoxItem container, bool after)
    {
        if (_queueDropTarget == container && _dropAfter == after) return;
        ClearQueueDropTarget();
        _queueDropTarget = container;
        _dropAfter = after;
        container.Tag = after ? "DropAfter" : "DropBefore";
    }

    private void ClearQueueDropTarget()
    {
        if (_queueDropTarget is not null) _queueDropTarget.Tag = null;
        _queueDropTarget = null;
        _dropAfter = false;
    }

    private static List<string> EnumerateAudioFiles(string root, HashSet<string> deckPaths, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PlaylistService.AudioExtensions.Contains(Path.GetExtension(file)) && !deckPaths.Contains(file)) result.Add(file);
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending.Push(child);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result;
    }

    private async void SavePlaylist_Click(object sender, RoutedEventArgs e) => await SavePlaylistAsync();

    private async Task<bool> SavePlaylistAsync()
    {
        if (SavePlaylistForTest is not null) return await SavePlaylistForTest();
        var slot = ViewModel.EditingQueueIndex;
        var dialog = new SaveFileDialog { Filter = "FloorballDJ-spellista|*.fdjplaylist.json", FileName = ViewModel.QueuePlaylistPath ?? "Ny spellista.fdjplaylist.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
        var document = new PlaylistDocument(
            Version: 2,
            ShuffleEnabled: ViewModel.QueueShuffleEnabled,
            LoopEnabled: ViewModel.QueueLoopEnabled,
            Entries: ViewModel.PlaybackQueue.Select(item => { var clip = ViewModel.GetQueueClipOptions(slot, item); return new PlaylistEntry(item.Title, item.FilePath, clip.UseRawFile, clip.EndSeconds); }).ToList(),
            TransitionType: (AutoplayTransitionType)ViewModel.QueueTransitionTypeIndex, TransitionSeconds: ViewModel.QueueTransitionSeconds,
            FadeInSeconds: ViewModel.QueueFadeInSeconds, FadeOutSeconds: ViewModel.QueueFadeOutSeconds);
        var vm = ViewModel; var project = vm.Project;
        try
        {
            await Playlists.SaveAsync(dialog.FileName, document);
            if (ReferenceEquals(project, vm.Project))
            {
                vm.InQueue(slot, () => vm.QueuePlaylistPath = Path.GetFullPath(dialog.FileName));
                return true;
            }
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Kunde inte spara spellistan", MessageBoxButton.OK, MessageBoxImage.Warning); }
        return false;
    }

    private async void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "FloorballDJ-spellista|*.fdjplaylist.json|JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!await ConfirmReplacementAsync()) return;
        try { await LoadPlaylistAsync(dialog.FileName, false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Kunde inte läsa spellistan", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public async Task<bool> LoadDefaultPlaylistAndStartAsync(string path, double volumeDb)
    {
        if (ViewModel.LoadedPlaylistIndex(path) is { } loaded)
        {
            CancelPlaylistPreparation();
            return await ViewModel.ResumeLoadedPlaylistAsync(loaded, volumeDb);
        }
        ViewModel.EditingQueueIndex = ViewModel.ShortcutQueueIndex();
        if (!await ConfirmReplacementAsync()) return false;
        _refreshCancellation?.Cancel();
        var viewModel = ViewModel;
        var project = viewModel.Project;
        var profileToken = viewModel.ProfileWorkCancellation;
        var result = await LoadPlaylistAsync(path, true, volumeDb);
        var version = _playlistVersion;
        // Schedule after the initial Play request. Available-library work is never
        // a prerequisite for a saved-playlist hotkey.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (version == _playlistVersion && !profileToken.IsCancellationRequested && ReferenceEquals(DataContext, viewModel) &&
                ReferenceEquals(project, viewModel.Project) && viewModel.AutoplayModeActive) _ = RefreshAvailableAsync();
        }));
        return result;
    }

    internal async Task<bool> LoadPlaylistAsync(string path, bool startPlayback, double queueGainOffsetDb = 0)
    {
        var viewModel = ViewModel;
        var slot = viewModel.EditingQueueIndex;
        _playlistVersion++;
        var queueRevision = viewModel.QueueRevision;
        var project = viewModel.Project;
        var folder = viewModel.Settings.MusicFolderPath;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(viewModel.ProfileWorkCancellation);
        var token = cancellation.Token;
        Interlocked.Exchange(ref _playlistCancellation, cancellation)?.Cancel();
        var performance = PerformanceDiagnostics.BeginOperation("AutoplayLoadRequested");
        using var total = performance.Measure("AutoplayLoad");
        try
        {
            var prepared = await Playlists.PrepareAsync(path, startPlayback ? folder : null,
                allowMissing: startPlayback, forceFresh: !startPlayback, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(DataContext, viewModel) || !ReferenceEquals(project, viewModel.Project) ||
                folder != viewModel.Settings.MusicFolderPath || queueRevision != viewModel.QueueRevision || (startPlayback && !viewModel.AutoplayModeActive))
                throw new OperationCanceledException(token);
            if (prepared is null) return false;
            Jingle[] items;
            using (performance.Measure("PlaylistQueueApply"))
            {
                // Resolve live deck references after the await: edits to clips/effects
                // during preparation must be reflected in the new queue.
                var known = viewModel.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio)
                    .DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
                if (!startPlayback && ReferenceEquals(_folderProject, project) && _folderPath == folder)
                    foreach (var item in _folderItems) known.TryAdd(item.FilePath, item);
                items = prepared.ExistingEntries.Select(entry =>
                {
                    if (known.TryGetValue(entry.FilePath, out var deckItem)) return deckItem;
                    var item = new Jingle { Title = entry.Title, FilePath = entry.FilePath, PlayMode = JinglePlayMode.Solo };
                    if (prepared.FolderFiles.TryGetValue(entry.FilePath, out var folderFile))
                    {
                        item = RawFile(folderFile);
                        known.Add(entry.FilePath, item);
                    }
                    return item;
                }).ToArray();
                viewModel.InQueue(slot, () =>
                {
                    viewModel.QueueShuffleEnabled = prepared.Definition.ShuffleEnabled;
                    viewModel.QueueLoopEnabled = prepared.Definition.LoopEnabled;
                    viewModel.ReplaceQueue(items, queueGainOffsetDb);
                    viewModel.QueueTransitionTypeIndex = (int)prepared.Definition.TransitionType;
                    viewModel.QueueTransitionSeconds = prepared.Definition.TransitionSeconds ?? 4;
                    viewModel.QueueFadeInSeconds = prepared.Definition.FadeInSeconds ?? 2;
                    viewModel.QueueFadeOutSeconds = prepared.Definition.FadeOutSeconds ?? 2;
                    for (var i = 0; i < items.Length; i++)
                    {
                        var entry = prepared.ExistingEntries[i];
                        var end = entry.EndSeconds;
                        var start = entry.UseRawFile ? 0 : items[i].StartSeconds;
                        if (end is { } seconds && (!double.IsFinite(seconds) || seconds <= start || (items[i].DurationSeconds > 0 && seconds > items[i].DurationSeconds))) end = null;
                        if (entry.UseRawFile || end.HasValue) viewModel.SetQueueClipOptions(slot, items[i], entry.UseRawFile, end);
                    }
                    viewModel.QueuePlaylistPath = Path.GetFullPath(path);
                });
            }
            // The start itself must not cancel its completed preparation.
            Interlocked.CompareExchange(ref _playlistCancellation, null, cancellation);
            return items.Length > 0 && (!startPlayback || viewModel.PlayNextFromQueue(slot, false));
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally
        {
            Interlocked.CompareExchange(ref _playlistCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }


}
