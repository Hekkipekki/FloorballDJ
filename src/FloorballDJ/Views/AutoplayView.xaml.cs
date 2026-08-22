using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloorballDJ.Models;
using FloorballDJ.ViewModels;
using Microsoft.Win32;

namespace FloorballDJ.Views;

public partial class AutoplayView : UserControl
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac", ".mp4", ".ogg" };
    private readonly ObservableCollection<Jingle> _filtered = [];
    private readonly ObservableCollection<DeckFilter> _filters = [];
    private readonly List<Jingle> _folderItems = [];
    private Point _queueDragStart;
    private Jingle? _queueDragItem;
    private ListBoxItem? _queueDropTarget;
    private bool _dropAfter;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _filterCancellation;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public AutoplayView()
    {
        InitializeComponent();
        AvailableList.ItemsSource = _filtered;
        DeckFilterTabs.ItemsSource = _filters;
        Unloaded += (_, _) =>
        {
            _refreshCancellation?.Cancel();
            _filterCancellation?.Cancel();
        };
    }

    public async Task RefreshAvailableAsync()
    {
        if (DataContext is not MainViewModel) return;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var selectedName = (DeckFilterTabs.SelectedItem as DeckFilter)?.Name ?? "Alla";
        _filters.Clear();
        _filters.Add(new DeckFilter("Alla", null));
        foreach (var deck in ViewModel.Decks.Take(ViewModel.Settings.DeckCount)) _filters.Add(new DeckFilter(deck.Name, deck));
        DeckFilterTabs.SelectedItem = _filters.FirstOrDefault(item => item.Name == selectedName) ?? _filters[0];

        _folderItems.Clear();
        var folder = ViewModel.Settings.MusicFolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                var deckPaths = ViewModel.Decks.SelectMany(deck => deck.Jingles).Select(jingle => jingle.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var paths = await Task.Run(() => EnumerateAudioFiles(folder, deckPaths, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var path in paths)
                    _folderItems.Add(new Jingle { Title = Path.GetFileNameWithoutExtension(path), FilePath = path, PlayMode = JinglePlayMode.Solo });
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
        FolderText.Text = string.IsNullOrWhiteSpace(folder) ? "Ingen musikmapp vald" : $"Musikmapp: {new DirectoryInfo(folder).Name}";
        ApplyFilter();
    }

    private IEnumerable<Jingle> SelectedSource()
    {
        if (DeckFilterTabs.SelectedItem is DeckFilter { Deck: not null } selected)
            return selected.Deck.Jingles.Where(jingle => jingle.HasAudio);
        return ViewModel.Decks.Take(ViewModel.Settings.DeckCount).SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio)
            .Select(jingle => RawFile(jingle.FilePath)).Concat(_folderItems.Select(jingle => RawFile(jingle.FilePath)))
            .DistinctBy(jingle => jingle.FilePath, StringComparer.OrdinalIgnoreCase);
    }

    private static Jingle RawFile(string path) => new()
    {
        Title = Path.GetFileNameWithoutExtension(path), FilePath = path, StartSeconds = 0, EndSeconds = null,
        DurationSeconds = 0, GainDb = 0, PitchSemitones = 0, TempoPercent = 0, RatePercent = 0,
        FadeInOverrideSeconds = null, FadeOutOverrideSeconds = null, PlayMode = JinglePlayMode.Solo
    };

    private void ApplyFilter()
    {
        if (DataContext is not MainViewModel) return;
        var query = SearchBox.Text.Trim();
        _filtered.Clear();
        foreach (var item in SelectedSource().Where(item => string.IsNullOrWhiteSpace(query) || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).OrderBy(item => item.Title))
            _filtered.Add(item);
    }

    private async void ChooseMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Välj mappen där musiken sparas", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ViewModel.Settings.MusicFolderPath = dialog.FolderName;
        await RefreshAvailableAsync();
        try { await ViewModel.SaveAsync(); } catch { }
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e) => AddSelected();
    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelected();
    private void AddSelected() { if (AvailableList.SelectedItem is Jingle item) ViewModel.AddToQueue(item); }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Jingle item) ViewModel.RemoveFromQueue(item); }
    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected_Click(sender, e);
    private void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || QueueList.SelectedItem is not Jingle item) return;
        ViewModel.RemoveFromQueue(item);
        e.Handled = true;
    }
    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is not Jingle item || !ReferenceEquals(item, ViewModel.ActiveQueueItem)) return;
        QueueList.Dispatcher.BeginInvoke(() => QueueList.ScrollIntoView(item));
    }
    private void PlaySelected_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Jingle item) ViewModel.PlayQueuedItem(item); }
    private void MoveUp_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Jingle item) { ViewModel.MoveQueueItem(item, -1); QueueList.SelectedItem = item; } }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Jingle item) { ViewModel.MoveQueueItem(item, 1); QueueList.SelectedItem = item; } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAvailableAsync();
    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _filterCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        try
        {
            await Task.Delay(140, cancellation.Token);
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
    }
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
    private void DeckFilterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void DeckFilterNext_Click(object sender, RoutedEventArgs e)
    {
        if (_filters.Count == 0) return;
        var next = (DeckFilterTabs.SelectedIndex + 1) % _filters.Count;
        DeckFilterTabs.SelectedIndex = next;
        DeckFilterTabs.ScrollIntoView(_filters[next]);
    }

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

    private void AvailableList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || !ViewModel.UseSecondaryOutput) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Jingle item) ViewModel.PlayPreview(item);
        e.Handled = true;
    }

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Jingle item) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ViewModel.UseSecondaryOutput)
        {
            ViewModel.PlayPreview(item);
            e.Handled = true;
            return;
        }
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
        var sourceContainer = QueueList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        if (sourceContainer is not null) sourceContainer.Opacity = 0.42;
        try { DragDrop.DoDragDrop(QueueList, new DataObject(typeof(Jingle), item), DragDropEffects.Move); }
        finally
        {
            if (sourceContainer is not null) sourceContainer.Opacity = 1;
            ClearQueueDropTarget();
        }
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Jingle)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move)
        {
            var container = ItemsControl.ContainerFromElement(QueueList, e.OriginalSource as DependencyObject) as ListBoxItem;
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
        if (!QueueList.IsMouseOver) ClearQueueDropTarget();
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Jingle)) is not Jingle source) return;
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
                    if (AudioExtensions.Contains(Path.GetExtension(file)) && !deckPaths.Contains(file)) result.Add(file);
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result;
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "FloorballDJ-spellista|*.fdjplaylist.json", FileName = "Ny spellista.fdjplaylist.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(ViewModel.PlaybackQueue.Select(item => new PlaylistEntry(item.Title, item.FilePath)), new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "FloorballDJ-spellista|*.fdjplaylist.json|JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<PlaylistEntry>>(File.ReadAllText(dialog.FileName)) ?? [];
            var all = SelectedSource().Concat(_folderItems).ToList();
            ViewModel.ReplaceQueue(entries.Where(entry => File.Exists(entry.FilePath)).Select(entry =>
                all.FirstOrDefault(item => string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)) ??
                new Jingle { Title = entry.Title, FilePath = entry.FilePath, PlayMode = JinglePlayMode.Solo }));
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Kunde inte läsa spellistan", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private sealed record DeckFilter(string Name, Deck? Deck);
    private sealed record PlaylistEntry(string Title, string FilePath);
}
