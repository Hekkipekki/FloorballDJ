using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class AutoplayAudioChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var dir = Path.Combine(root, "autoplay-audio"); Directory.CreateDirectory(dir);
        var projects = new ProjectService(); using var audio = new AudioEngine();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        vm.Settings.MasterVolumeDb = -60; vm.QueueTransitionSeconds = 0; vm.QueueLoopEnabled = false; vm.SetAutoplayMode(true);
        var a = Song(dir, "Decklåt", 6); a.StartSeconds = 1; a.EndSeconds = 4;
        var b = Song(dir, "Nästa låt", 6);
        vm.ReplaceQueue([a, b]); vm.PlayQueuedItem(a); audio.PublishSnapshot();
        Check(vm.NowPlaying.Duration.TotalSeconds == 3, "Deck version uses its stored start/end");
        audio.StopAll(); vm.SetQueueClipOptions(0, a, true, null); vm.PlayQueuedItem(a); audio.PublishSnapshot();
        Check(vm.NowPlaying.Duration.TotalSeconds == 6 && vm.NowPlaying.Position.TotalSeconds < .3, "Raw file ignores both deck bounds");
        Check(a.StartSeconds == 1 && a.EndSeconds == 4, "Raw playback never mutates the deck jingle");
        vm.SetQueueClipOptions(0, a, true, 1.3); audio.PublishSnapshot();
        Check(vm.NowPlaying.Duration.TotalSeconds == 1.3 && a.EndSeconds == 4, "A custom playlist end updates current playback without changing the deck");
        await Until(() => vm.ActiveQueueItem == b, "Custom end naturally advances to the next song");
        audio.StopAll(); vm.SetQueueClipOptions(0, a, false, 2.5); vm.PlayQueuedItem(a); audio.PublishSnapshot();
        Check(vm.NowPlaying.Duration.TotalSeconds == 1.5, "Custom end retains the deck start in deck mode");
        a.PlayMode = JinglePlayMode.Mix; b.PlayMode = JinglePlayMode.Duck;
        audio.StopAll(); vm.ReplaceQueue([a, b]); vm.QueueTransitionTypeIndex = 0; vm.QueueTransitionSeconds = .5;
        vm.PlayQueuedItem(a); await Task.Delay(80); Check(vm.PlayNextAutoplay(), "Crossfade starts");
        Check(vm.ActiveQueueItem == b && Voices(audio).Length == 2, "Crossfade overlaps outgoing and incoming audio voices");
        await Until(() => Voices(audio).Length == 1, "Crossfade disposes only the outgoing voice after fading");
        Check(a.PlayMode == JinglePlayMode.Mix && b.PlayMode == JinglePlayMode.Duck, "Autoplay transitions preserve the deck playback modes");
        audio.StopAll(); vm.ReplaceQueue([a, b]); vm.QueueTransitionTypeIndex = 1; vm.QueueFadeOutSeconds = .3; vm.QueueFadeInSeconds = .2;
        vm.PlayQueuedItem(a); await Task.Delay(70); Check(vm.PlayNextAutoplay(), "Sequential fades start");
        Check(vm.ActiveQueueItem == a && Voices(audio).Length == 1, "Sequential fade does not start the incoming song during fade-out");
        await Until(() => vm.ActiveQueueItem == b, "Sequential fade starts the successor after fade-out");
        Check(Voices(audio).Length == 1, "Sequential fade-in has no overlapping outgoing voice");
        audio.StopAll(); vm.ReplaceQueue([a, b]); vm.PlayQueuedItem(a); vm.PlayNextAutoplay(); vm.CancelAutoplayTransition(); audio.StopAll();
        await Task.Delay(450); Check(vm.ActiveQueueItem != b && Voices(audio).Length == 0, "Stop cancels a pending sequential start");
        foreach (var slot in new[] { 0, 1 })
        {
            vm.EditingQueueIndex = slot; vm.ReplaceQueue([a, b]); vm.QueueTransitionTypeIndex = 1; vm.QueueFadeOutSeconds = .15;
            vm.QueueFadeInSeconds = .1; vm.QueueLoopEnabled = false;
            vm.SetQueueClipOptions(slot, a, true, .65); vm.PlayQueuedItem(a);
            vm.EditingQueueIndex = 1 - slot;
            await Until(() => vm.ActiveQueueItem == b, "Automatic sequential fades retain source ownership while the other list is selected");
            Check(Voices(audio).Length == 1, "Automatic fade-in follows completed fade-out"); audio.StopAll();
        }

        // Saved playlists, profile restarts and shortcut destination choice.
        var file = Path.Combine(dir, "Paus.fdjplaylist.json");
        await PlaylistService.Shared.SaveAsync(file, new(3, false, false, [new(a.Title, a.FilePath, true, 2.2), new(b.Title, b.FilePath)], AutoplayTransitionType.FadeInOut, .7, .2, .4));
        vm.SetAutoplayMode(false); vm.SetAutoplayMode(true); vm.EditingQueueIndex = 0; vm.ReplaceQueue([a, b]); vm.QueueTransitionSeconds = 0; vm.QueueTransitionTypeIndex = 1; vm.QueueFadeOutSeconds = .4; vm.PlayQueuedItem(a); vm.PlayNextAutoplay();
        vm.EditingQueueIndex = 1; vm.ReplaceQueue([]); vm.EditingQueueIndex = 0;
        var prompts = 0;
        var view = new AutoplayView { DataContext = vm, ConfirmPlaylistReplacement = () => { prompts++; return UnsavedProfileChoice.Discard; } };
        Check(await view.LoadDefaultPlaylistAndStartAsync(file, 0), "Shortcut loads the saved playlist");
        Check(vm.EditingQueueIndex == 1 && vm.LeftPlaybackQueue.SequenceEqual(new[] { a, b }) && vm.RightPlaybackQueue.Count == 2,
            "Shortcut selects the empty side and preserves the playing list contents");
        Check(prompts == 0, "Shortcut into the empty side never opens a save prompt");
        Check(vm.QueueTransitionTypeIndex == 1 && vm.QueueFadeInSeconds == .2 && vm.QueueFadeOutSeconds == .4 && vm.GetQueueClipOptions(1, vm.RightPlaybackQueue[0]).UseRawFile,
            "Playlist files retain raw/custom-end and transition settings");
        Check(vm.PlayNextAutoplay(), "Shortcut cancels the previous side’s pending sequential transition");
        await Until(() => vm.ActiveQueueItem?.FilePath == b.FilePath, "New source still advances after switching during a fade");
        Check(vm.ShortcutQueueIndex() == 0, "With both sides populated, the next shortcut targets the nonplaying side");
        audio.StopAll();
        vm.RightPlaybackQueue.Clear();
        Check(vm.ShortcutQueueIndex() == 1, "An empty playlist remains the first choice after playback has stopped");
        await view.LoadPlaylistAsync(file, false);
        var path = Path.Combine(dir, "Audio.floorballdj.json"); await vm.SaveAsync(path); await vm.LoadAsync(path); vm.EditingQueueIndex = 1;
        Check(vm.QueueTransitionTypeIndex == 1 && vm.GetQueueClipOptions(1, vm.RightPlaybackQueue[0]).EndSeconds == 2.2, "Profile reload retains independent playlist audio choices");
        Check(vm.GetQueueClipOptions(0, vm.LeftPlaybackQueue[0]).EndSeconds != 2.2, "The same song has independent choices in the other playlist");
        Check(AutoplayClipWindow.TryParse("2:03,5", out var seconds) && seconds == 123.5 && !AutoplayClipWindow.TryParse("1:75", out _) && !AutoplayClipWindow.TryParse("NaN", out _), "End-time entry accepts local decimals and rejects invalid times");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            var window = new Window { Content = view, Width = 1240, Height = 740, ShowInTaskbar = false };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var left = (ListBox)view.FindName("QueueList");
                var song = vm.LeftPlaybackQueue[0];
                var raw = Children(left).OfType<CheckBox>().First();
                raw.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                raw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(vm.GetQueueClipOptions(0, song).UseRawFile && song.StartSeconds == 1 && song.EndSeconds == 4, "The row checkbox updates only playlist options");
                song.DurationSeconds = 1; // The on-disk file is six seconds; cached profile metadata may be stale.
                left.ContextMenu.Tag = (0, song);
                var edited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                poll.Tick += async (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<AutoplayClipWindow>().FirstOrDefault(item => item.IsLoaded);
                    if (dialog is null) return;
                    poll.Stop();
                    try
                    {
                        ((TextBox)dialog.FindName("EndTimeBox")).Text = "0:02.9";
                        await Dispatcher.Yield(DispatcherPriority.Render);
                        Save((FrameworkElement)dialog.Content, Path.Combine(output, "autoplay-end-editor.png"));
                        ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        edited.TrySetResult();
                    }
                    catch (Exception ex) { edited.TrySetException(ex); dialog.Close(); }
                };
                poll.Start();
                try
                {
                    ((MenuItem)left.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await edited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => vm.GetQueueClipOptions(0, song).EndSeconds == 2.9, "The actual context-menu editor applies the custom end");
                }
                finally { poll.Stop(); }
                vm.EditingQueueIndex = 1;
                Save((FrameworkElement)window.Content, Path.Combine(output, "autoplay-audio-options.png"));
                window.Width = 1040; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Save((FrameworkElement)window.Content, Path.Combine(output, "autoplay-audio-options-1040.png"));
                ((ComboBox)view.FindName("TransitionTypeCombo")).SelectedIndex = 0;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(vm.IsQueueCrossfade && vm.QueueTransitionTypeIndex == 0, "The actual transition dropdown changes the selected playlist mode");
                Save((FrameworkElement)window.Content, Path.Combine(output, "autoplay-crossfade-1040.png"));
            }
            finally { window.Close(); }
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        audio.StopAll(); vm.SetAutoplayMode(false); vm.SetAutoplayMode(true);
        vm.EditingQueueIndex = 0; vm.ReplaceQueue([]); vm.QueuePlaylistPath = null;
        vm.EditingQueueIndex = 1; vm.ReplaceQueue([]); vm.QueuePlaylistPath = null;
        prompts = 0;
        Check(await view.LoadDefaultPlaylistAndStartAsync(file, 0) && prompts == 0 && vm.LeftPlaybackQueue.Count == 2 && vm.RightPlaybackQueue.Count == 0,
            "Two fresh empty playlists load and start a shortcut directly without prompting");
        var retained = vm.LeftPlaybackQueue.ToArray();
        vm.Settings.FadeOutSeconds = .03; vm.Settings.FadeInSeconds = .03; vm.ConfigureAudio();
        await Until(() => vm.NowPlaying.Position.TotalSeconds > .2, "Loaded shortcut begins playback");
        var starts = 0;
        void Started(object? sender, EventArgs e) => starts++;
        vm.PrimaryPlaybackStarted += Started;
        var position = vm.NowPlaying.Position;
        Check(await view.LoadDefaultPlaylistAndStartAsync(file.ToUpperInvariant(), 0) && prompts == 0 && starts == 0 &&
            vm.EditingQueueIndex == 0 && vm.LeftPlaybackQueue.SequenceEqual(retained) && vm.RightPlaybackQueue.Count == 0 && vm.NowPlaying.Position >= position,
            "Repeated shortcut focuses the loaded side without restarting, reloading, prompting or duplicating it");
        await audio.PauseOrResumeAsync(); audio.PublishSnapshot();
        Check(vm.NowPlaying.IsPaused, "Resume fixture pauses the actual playing voice");
        Check(await view.LoadDefaultPlaylistAndStartAsync(file, 0) && starts == 0 && prompts == 0, "Shortcut unpauses the existing voice without a restart");
        audio.PublishSnapshot(); Check(!vm.NowPlaying.IsPaused, "Shortcut resumes paused playback");
        vm.PrimaryPlaybackStarted -= Started;
        position = vm.NowPlaying.Position;
        vm.EditingQueueIndex = 1; vm.ReplaceQueue([a, b]);
        var otherFile = Path.Combine(dir, "Annan spellista.fdjplaylist.json");
        await PlaylistService.Shared.SaveAsync(otherFile, new(3, false, false, [new(b.Title, b.FilePath), new(a.Title, a.FilePath)], AutoplayTransitionType.Crossfade, 0));
        Check(await view.LoadDefaultPlaylistAndStartAsync(otherFile, 0) && prompts == 1 && vm.LeftPlaybackQueue.SequenceEqual(retained),
            "A populated inactive side still prompts and preserves the other playlist");
        Check(await view.LoadDefaultPlaylistAndStartAsync(file, 0) && prompts == 1 && vm.EditingQueueIndex == 0 && vm.LeftPlaybackQueue.SequenceEqual(retained),
            "Returning shortcut reuses the original side with its edited queue intact");
        audio.PublishSnapshot();
        Check(vm.NowPlaying.JingleId == retained[0].Id && vm.NowPlaying.Position.TotalSeconds >= position.TotalSeconds - .1,
            "Returning to an inactive loaded playlist resumes its former song position");
        Check(vm.PlayNextAutoplay(false) && vm.ActiveQueueItem == retained[1], "A resumed playlist continues after its remembered row");
        vm.EditingQueueIndex = 0; vm.ReplaceQueue([]);
        Check(await view.ConfirmReplacementAsync() && prompts == 1, "An emptied playlist with an existing path also replaces without prompting");
        audio.StopAll(); vm.SetAutoplayMode(false);
        Console.WriteLine("PASS: raw/deck clips and live custom end without source mutation; real overlapping crossfade versus sequential fades, cancellation and source isolation; saved settings and inactive shortcut destination.");
    }
    private static Jingle Song(string root, string name, double seconds)
    {
        var path = Path.Combine(root, name + ".wav"); using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) { var bytes = new byte[(int)(192000 * seconds)]; writer.Write(bytes, 0, bytes.Length); }
        return new() { Title = name, FilePath = path, DurationSeconds = seconds };
    }
    private static object[] Voices(AudioEngine audio) => ((IEnumerable)typeof(AudioEngine).GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!).Cast<object>().ToArray();
    private static async Task Until(Func<bool> condition, string message) { var deadline = DateTime.UtcNow.AddSeconds(7); while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10); Check(condition(), message); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Children(child)) yield return nested; } }
    private static void Save(FrameworkElement element, string path) { var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackgroundBrush"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight)); dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight)); } bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
