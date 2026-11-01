using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class AutoplayEditingChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var directory = Path.Combine(root, "autoplay-editing"); Directory.CreateDirectory(directory);
        await CheckQueue(directory);
        await CheckShuffleToSequential(directory);
        await AutoplayAudioChecks.RunAsync(directory, output);
        await DualAutoplayChecks.RunAsync(directory, output);
        await CheckPicker(directory, output);
        Console.WriteLine("PASS: edited Autoplay order for manual next, natural completion and crossfade; add/remove/current/duplicate/loop cases; live deck/page checkmarks and return; quiet themed exit and selected deck restoration.");
    }

    private static async Task CheckQueue(string root)
    {
        using var audio = new AudioEngine();
        var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        vm.Settings.MasterVolumeDb = -60;
        vm.QueueTransitionSeconds = 0; vm.QueueLoopEnabled = false; vm.SetAutoplayMode(true);
        var songs = Enumerable.Range(0, 5).Select(i => Song(root, $"song-{i}", 3)).ToArray();
        var (a, b, c, d, extra) = (songs[0], songs[1], songs[2], songs[3], songs[4]);
        void Start() { audio.StopAll(); vm.ReplaceQueue([a, b, c, d]); vm.PlayQueuedItem(b); Check(vm.ActiveQueueItem == b, "Queue starts on the second item"); }
        void NextIs(Jingle expected, string message) { Check(vm.NextQueuePrefetchPath() == expected.FilePath, message + " (prefetch)"); Check(vm.PlayNextQueued(true) && vm.ActiveQueueItem == expected, message); }
        Start(); vm.MoveQueueItem(d, -1); NextIs(d, "Moving a future song before the old next song follows the edited order");
        Start(); vm.MoveQueueItem(b, 1); NextIs(d, "Moving the playing song follows its new successor");
        Start(); vm.MoveQueueItem(a, 2); NextIs(c, "Moving a preceding song below the playing song does not restart");
        Start(); vm.AddToQueue(extra); NextIs(c, "Appending during playback preserves the successor");
        Start(); vm.RemoveFromQueue(a); NextIs(c, "Removing a preceding song preserves the successor");
        Start(); vm.RemoveFromQueue(c); NextIs(d, "Removing the upcoming song advances to the following song");
        Start(); vm.RemoveFromQueue(b); NextIs(c, "Removing the playing item retains its former successor");
        vm.ReplaceQueue([a, b, a, c]); vm.PlayQueuedItem(b); Check(vm.PlayNextQueued() && vm.ActiveQueueItem == a, "Second duplicate occurrence starts");
        vm.MoveQueueItem(a, 3); NextIs(c, "Moving the first duplicate preserves the playing second occurrence");
        Start(); vm.PlayQueuedItem(d); vm.AddToQueue(extra); NextIs(extra, "Appending after a formerly final playing song makes it the successor");
        Check(!vm.PlayNextQueued(), "Non-looping edited queue stops at the end");
        vm.QueueLoopEnabled = true; NextIs(a, "Only an enabled loop wraps at the end"); vm.QueueLoopEnabled = false;
        Start(); vm.QueueShuffleEnabled = true; vm.MoveQueueItem(d, -1);
        Check(vm.PlayNextQueued() && vm.ActiveQueueItem != b, "Shuffle still selects another playable song"); vm.QueueShuffleEnabled = false;

        // Real, silent WASAPI playback covers both completion callbacks and the
        // timer-driven transition path after edits, rather than faking the event.
        foreach (var transition in new[] { 0d, .3d })
        {
            audio.StopAll();
            var shortSong = Song(root, $"short-{transition}", .7);
            vm.QueueTransitionSeconds = transition;
            vm.ReplaceQueue([a, shortSong, c, d]); vm.PlayQueuedItem(shortSong); vm.MoveQueueItem(d, -1);
            await Until(() => vm.ActiveQueueItem == d, "Automatic advance follows edited order");
            Check(vm.PlaybackQueue.SequenceEqual(new[] { a, shortSong, d, c }), "Automatic advance retains edited queue");
        }
        audio.StopAll(); vm.QueueTransitionSeconds = 0;
        var removed = Song(root, "removed-current", .5);
        vm.ReplaceQueue([a, removed, c]); vm.PlayQueuedItem(removed); vm.RemoveFromQueue(removed);
        await Until(() => vm.ActiveQueueItem == c, "A removed current song still advances when it finishes");
        audio.StopAll(); vm.SetAutoplayMode(false);
    }

    private static async Task CheckShuffleToSequential(string root)
    {
        using var audio = new AudioEngine();
        var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        vm.Settings.MasterVolumeDb = -60; vm.Settings.TrackSession = true; vm.SetAutoplayMode(true);
        var songs = Enumerable.Range(0, 5).Select(i => Song(root, $"shuffle-order-{i}", 10)).ToArray();
        var shortSong = Song(root, "shuffle-to-sequential-short", .8);
        foreach (var slot in new[] { 0, 1 })
        foreach (var mode in new[] { "manual", "completion", "crossfade" })
        {
            audio.StopAll(); vm.EditingQueueIndex = slot;
            var target = mode == "manual" ? songs[2] : shortSong;
            vm.ReplaceQueue([songs[0], songs[1], target, songs[3], songs[4]]);
            vm.QueueLoopEnabled = false; vm.QueueShuffleEnabled = false;
            vm.QueueTransitionSeconds = mode == "crossfade" ? .3 : 0;
            foreach (var item in vm.PlaybackQueue) item.SessionPlayCount = 1;
            target.SessionPlayCount = 0; // Exactly one unplayed candidate makes the random choice deterministic.
            vm.PlayQueuedItem(songs[0]); vm.QueueShuffleEnabled = true;
            Check(vm.PlayNextAutoplay(false) && vm.ActiveQueueItem == target, "Shuffle jumps away from the former sequential cursor");
            vm.QueueShuffleEnabled = false;
            Check(vm.NextQueuePrefetchPath() == songs[3].FilePath, "Turning shuffle off prepares the successor of the randomly selected song");
            vm.EditingQueueIndex = 1 - slot;
            if (mode == "manual") Check(vm.PlayNextAutoplay() && vm.ActiveQueueItem == songs[3], "Manual next after shuffle follows the actual playing position in its source list");
            else await Until(() => vm.ActiveQueueItem == songs[3], "Natural completion/crossfade after shuffle follows the actual playing position in its source list");
        }
        audio.StopAll(); vm.EditingQueueIndex = 0; vm.QueueTransitionSeconds = 0;
        vm.ReplaceQueue(songs); vm.QueueShuffleEnabled = false; vm.QueueLoopEnabled = false;
        foreach (var item in songs) item.SessionPlayCount = 1;
        songs[^1].SessionPlayCount = 0;
        vm.PlayQueuedItem(songs[0]); vm.QueueShuffleEnabled = true;
        Check(vm.PlayNextQueued() && vm.ActiveQueueItem == songs[^1], "Shuffle can select the final row");
        vm.QueueShuffleEnabled = false;
        Check(vm.NextQueuePrefetchPath() is null && !vm.PlayNextAutoplay(), "Sequential playback stops after a shuffled final row when loop is off");
        vm.QueueLoopEnabled = true;
        Check(vm.PlayNextAutoplay() && vm.ActiveQueueItem == songs[0], "Sequential playback wraps after a shuffled final row only with loop enabled");
        audio.StopAll(); vm.QueueLoopEnabled = false; vm.ReplaceQueue(songs);
        foreach (var item in songs) item.SessionPlayCount = 1;
        songs[^1].SessionPlayCount = 0;
        vm.PlayQueuedItem(songs[0]); vm.QueueShuffleEnabled = true;
        Check(vm.PlayNextQueued() && vm.ActiveQueueItem == songs[^1], "Non-looping shuffle can jump to the final row without ending its pass early");
        vm.Resume(songs[^1], TimeSpan.FromSeconds(.1), queuedItem: true);
        for (var i = 0; i < songs.Length - 2; i++) Check(vm.PlayNextQueued(), "Shuffle pass length remains independent of the selected row index");
        Check(!vm.PlayNextQueued(), "Non-looping shuffle retains its bounded pass length");
        audio.StopAll(); vm.SetAutoplayMode(false);
        Console.WriteLine("PASS: shuffle-to-sequential manual next, natural completion and crossfade in both playlists; final-row loop policy and bounded shuffle pass.");
    }

    private static async Task CheckPicker(string root, string? output)
    {
        var previousRoot = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var previousMain = Application.Current.MainWindow;
        var language = LanguageService.CurrentLanguage;
        var fixture = Path.Combine(root, "picker"); Directory.CreateDirectory(fixture);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", fixture);
        MainWindow? main = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var projects = new ProjectService();
            var project = RandomSettingsChecks.Fixture(fixture, 48, 2);
            project.Settings.DeckCount = 2;
            var paged = project.Decks[0]; paged.Rows = 2; paged.Columns = 3; paged.PageCount = 2;
            paged.PageLayouts = [new() { Rows = 2, Columns = 3 }, new() { Rows = 2, Columns = 3 }];
            while (paged.Jingles.Count > 12) paged.Jingles.RemoveAt(paged.Jingles.Count - 1);
            ProjectService.EnsureLayout(project);
            await projects.SaveAsync(project, projects.DefaultProjectPath);
            main = new MainWindow(new LicenseService(), false) { ShowInTaskbar = false, ConfirmUnsavedProfile = () => UnsavedProfileChoice.Discard };
            main.Closed += (_, _) => closed.TrySetResult(); Application.Current.MainWindow = main;
            main.Show(); await Until(() => main.StartupReady, "Picker main window starts");
            var vm = (MainViewModel)main.DataContext;
            var view = (AutoplayView)main.FindName("EmbeddedAutoplay");
            var tabs = (TabControl)main.FindName("DeckTabsControl");
            var finish = (Button)main.FindName("FinishSongPickerButton");
            var choose = (Button)view.FindName("ChooseDeckSongsButton");
            var first = vm.Decks[0].Jingles.First(j => j.HasAudio);
            var secondPage = vm.Decks[0].Jingles.Skip(6).First(j => j.HasAudio);
            var otherDeck = vm.Decks[1].Jingles.First(j => j.HasAudio);
            first.FilePath = Song(fixture, "picker-playing", 30).FilePath; first.DurationSeconds = 30; first.StartSeconds = 0; first.EndSeconds = 30;
            vm.Settings.MasterVolumeDb = -60; vm.QueueTransitionSeconds = 0; vm.ReplaceQueue([first]);
            Call(main, "Autoplay_Click", main, new RoutedEventArgs()); await Idle();
            vm.PlayQueuedItem(first); var playing = vm.ActiveQueueItem;
            foreach (var locale in new[] { "sv", "en" })
            {
                LanguageService.SetLanguage(locale); LanguageService.TranslateTree(main);
                choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(vm.AutoplayModeActive && vm.AutoplaySongPickerActive && view.Visibility == Visibility.Collapsed && tabs.SelectedContent is Deck,
                    "Picker displays the existing populated deck view while keeping Autoplay active");
                Check(vm.ActiveQueueItem == playing, "Entering picker leaves playback tracking intact");
                first.SessionPlayCount = 3;
                tabs.SelectedIndex = 0; vm.Decks[0].ActivePage = 0; await Idle();
                Button Card(Jingle song) => Children(tabs).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, song) && button.Content is Grid);
                Check(Children(Card(first)).OfType<Border>().Single(border => border.Name == "AutoplayPickerCheck").IsVisible, "Selected song has a visible corner checkbox");
                Check(!Children(Card(first)).OfType<Border>().Single(border => border.Name == "SessionPlayedBadge").IsVisible && first.SessionPlayCount == 3,
                    "Picker hides the green played indicator without clearing session history or the selection checkmark");
                var pageButton = Children(tabs).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(button => button.DataContext is int n && n == 2);
                pageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(vm.AutoplayModeActive && vm.AutoplaySongPickerActive && vm.Decks[0].ActivePage == 1, "Page navigation stays in picker without an exit prompt");
                Card(secondPage).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(vm.PlaybackQueue.Last() == secondPage && vm.ActiveQueueItem == playing, "Selecting a second-page song appends without playing it");
                var checkbox = Children(Card(secondPage)).OfType<Border>().Single(border => border.Name == "AutoplayPickerCheck");
                Check(Children(checkbox).OfType<TextBlock>().Single().IsVisible, "Selected song displays a checkmark");
                Card(secondPage).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(!vm.PlaybackQueue.Contains(secondPage) && !Children(checkbox).OfType<TextBlock>().Single().IsVisible, "Deselecting removes the song and clears the checkmark");
                Card(secondPage).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                tabs.SelectedIndex = 1; await Idle(); Card(otherDeck).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(vm.PlaybackQueue.Last() == otherDeck && vm.ActiveQueueItem == playing && vm.AutoplayModeActive, "Cross-deck selection retains playback and selection order");
                vm.Audio.PublishSnapshot();
                Check(vm.NowPlaying.JingleId == first.Id && vm.NowPlaying.Position > TimeSpan.Zero && !vm.NowPlaying.IsFadingOut,
                    "Actual primary playback continues while selecting songs");
                foreach (var (width, height) in new[] { (1460d, 860d), (1040d, 680d) })
                {
                    main.WindowState = WindowState.Normal; main.Width = width; main.Height = height; await Idle();
                    var position = finish.TransformToAncestor(main).Transform(new Point());
                    Check(finish.IsVisible && position.X >= 0 && position.X + finish.ActualWidth <= main.ActualWidth + 1, "Done button stays visible at desktop and laptop sizes");
                    if (output is not null) Save((FrameworkElement)main.Content, output, $"picker-{locale}-{width}.png");
                }
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(vm.AutoplayModeActive && !vm.AutoplaySongPickerActive && view.IsVisible && vm.ActiveQueueItem == playing, "Done restores Autoplay without interrupting or resetting playback");
                Check(((ListBox)view.FindName("QueueList")).Items.Cast<Jingle>().SequenceEqual(vm.PlaybackQueue), "Autoplay shows the same updated playlist");
                if (output is not null) Save(view, output, $"autoplay-{locale}.png");
                // Exercise the real modal dialog; the safe default retains mode.
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<AutoplayExitWindow>().Single();
                    LanguageService.TranslateTree(dialog);
                    Check(((Button)dialog.FindName("StayButton")).IsDefault, "Stay is the safe default");
                    if (output is not null) Save((FrameworkElement)dialog.Content, output, $"autoplay-exit-{locale}.png");
                    ((Button)dialog.FindName("StayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, DispatcherPriority.ApplicationIdle);
                Check(!main.TryLeaveAutoplay() && vm.AutoplayModeActive && view.IsVisible, "Themed dialog cancel retains Autoplay");
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<AutoplayExitWindow>().Single();
                    ((Button)dialog.FindName("LeaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, DispatcherPriority.ApplicationIdle);
                Check(main.TryLeaveAutoplay(), "Themed dialog confirms leaving"); await Idle();
                Check(!vm.AutoplayModeActive && view.Visibility == Visibility.Collapsed && tabs.SelectedContent is Deck && Children(tabs).OfType<Button>().Any(b => b.DataContext is Jingle),
                    "Leaving Autoplay always restores a populated selected deck");
                if (output is not null) Save((FrameworkElement)main.Content, output, $"after-autoplay-{locale}.png");
                // The next pass begins from an actual normal deck selection.
                vm.Decks[0].ActivePage = 0; tabs.SelectedIndex = 0;
                await Idle();
                Check(Children(tabs).OfType<Border>().Any(border => border.Name == "SessionPlayedBadge" && ReferenceEquals(border.DataContext, first) && border.IsVisible),
                    "The played indicator returns in the normal deck view");
                vm.Audio.StopAll();
                vm.ReplaceQueue([first]);
                Call(main, "Autoplay_Click", main, new RoutedEventArgs()); await Idle(); vm.PlayQueuedItem(first); playing = first;
            }
            main.ConfirmAutoplayExit = () => true;
            var targetTab = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(1);
            Call(main, "DeckTabs_PreviewMouseLeftButtonDown", tabs, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = targetTab });
            await Idle();
            Check(!vm.AutoplayModeActive && ReferenceEquals(tabs.SelectedContent, vm.Decks[1]) &&
                System.Windows.Data.BindingOperations.IsDataBound(tabs, System.Windows.Controls.Primitives.Selector.SelectedItemProperty),
                "Confirming a clicked tab selects that exact populated deck and retains its binding");
            Call(main, "Autoplay_Click", main, new RoutedEventArgs()); await Idle();
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Call(main, "MainWindow_PreviewKeyDown", main, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!vm.AutoplaySongPickerActive && vm.AutoplayModeActive && view.IsVisible, "Escape also returns to Autoplay");
            vm.EditingQueueIndex = 1; vm.ReplaceQueue([]); tabs.SelectedIndex = 0; vm.Decks[0].ActivePage = 0;
            var leftBefore = vm.LeftPlaybackQueue.ToArray();
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            tabs.SelectedIndex = 0; vm.Decks[0].ActivePage = 0; await Idle();
            main.SetPickerSelection(false, true);
            var firstPageCount = vm.Decks[0].Jingles.Take(6).Count(item => item.HasAudio && !item.IsTextBlock);
            Check(vm.RightPlaybackQueue.Count == firstPageCount && vm.LeftPlaybackQueue.SequenceEqual(leftBefore), "Whole-page selection targets only the chosen playlist");
            Call(main, "ToggleAutoplayPickerSong", first);
            Check(!vm.RightPlaybackQueue.Contains(first) && vm.RightPlaybackQueue.Count == firstPageCount - 1, "Bulk selection supports individual deselection");
            main.SetPickerSelection(true, true); main.SetPickerSelection(true, true);
            Check(vm.RightPlaybackQueue.Count == vm.Decks[0].Jingles.Count(item => item.HasAudio && !item.IsTextBlock), "Whole-deck selection includes all pages and is idempotent");
            main.SetPickerSelection(false, false);
            Check(vm.RightPlaybackQueue.All(item => vm.Decks[0].Jingles.Skip(6).Contains(item)), "Deselect page preserves the other pages");
            main.SetPickerSelection(true, false); Check(vm.RightPlaybackQueue.Count == 0, "Deselect deck removes its selected songs");
            main.SetPickerSelection(true, true); await Idle();
            if (output is not null) Save((FrameworkElement)main.Content, output, "autoplay-bulk-picker.png");
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            vm.EditingQueueIndex = 0;
            vm.Audio.StopAll();
        }
        finally
        {
            if (main is not null) { main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            Application.Current.MainWindow = previousMain;
            LanguageService.SetLanguage(language);
            Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previousRoot);
        }
    }

    private static Jingle Song(string root, string name, double seconds)
    {
        var path = Path.Combine(root, name + ".wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.Write(new byte[(int)(48000 * 4 * seconds)], 0, (int)(48000 * 4 * seconds));
        return new Jingle { Title = name, FilePath = path, DurationSeconds = seconds };
    }
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(condition(), message);
    }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(50); }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement element, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
