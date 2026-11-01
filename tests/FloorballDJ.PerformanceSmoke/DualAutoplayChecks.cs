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

internal static class DualAutoplayChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var directory = Path.Combine(root, "two-playlists"); Directory.CreateDirectory(directory);
        var projects = new ProjectService();
        using var audio = new AudioEngine();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), audio);
        vm.Settings.MasterVolumeDb = -60; vm.QueueTransitionSeconds = 0; vm.SetAutoplayMode(true);
        var a = Song(directory, "Vänster första", 20); var b = Song(directory, "Vänster andra", 20);
        var c = Song(directory, "Höger första", 20); var d = Song(directory, "Höger andra", 20);
        vm.ReplaceQueue([a, b]); vm.QueueLoopEnabled = false;
        vm.EditingQueueIndex = 1; vm.ReplaceQueue([c, d]); vm.QueueShuffleEnabled = true;
        Check(vm.QueueLoopEnabled && vm.QueueShuffleEnabled, "Second list starts with its own loop/shuffle state");
        vm.EditingQueueIndex = 0;
        Check(!vm.QueueLoopEnabled && !vm.QueueShuffleEnabled, "First list retains independent loop/shuffle state");
        vm.PlayQueuedItem(a); vm.EditingQueueIndex = 1;
        Check(vm.PlayNextAutoplay() && vm.ActiveQueueItem == b, "Manual transport next follows the playing list while the other list is selected");
        Check(!vm.PlayNextQueued(), "End of non-looping left list never starts the right list");
        audio.StopAll(); vm.QueueShuffleEnabled = false; vm.PlayQueuedItem(c); vm.EditingQueueIndex = 0;
        Check(vm.PlayNextAutoplay() && vm.ActiveQueueItem == d, "Right-list playback continues on the right while editing the left");
        audio.StopAll();
        foreach (var source in new[] { 0, 1 })
        foreach (var transition in new[] { 0d, .3d })
        {
            vm.EditingQueueIndex = source;
            var shortSong = Song(directory, $"short-{source}-{transition}", .8);
            var successor = source == 0 ? b : d;
            vm.ReplaceQueue([shortSong, successor]); vm.QueueShuffleEnabled = false; vm.QueueLoopEnabled = false;
            vm.QueueTransitionSeconds = transition; vm.PlayQueuedItem(shortSong);
            vm.EditingQueueIndex = 1 - source; vm.ReplaceQueue([source == 0 ? c : a]);
            await Until(() => vm.ActiveQueueItem == successor, "Natural completion/crossfade remains in its source after editing the other list");
            Check(vm.IsAutoplayQueued(successor) && !vm.IsAutoplayQueued(source == 0 ? c : a), "Space resume uses the playing list");
            vm.Resume(successor, TimeSpan.FromSeconds(.1), true);
            Check(vm.ActiveQueueItem == successor && !vm.PlayNextQueued(), "Resume retains source and non-looping boundary");
            audio.StopAll();
        }

        var choice = UnsavedProfileChoice.Cancel; var prompts = 0; var saves = 0; var saved = false;
        var view = new AutoplayView
        {
            DataContext = vm, ConfirmPlaylistReplacement = () => { prompts++; return choice; },
            SavePlaylistForTest = () => { saves++; return Task.FromResult(saved); }
        };
        var window = new Window { Content = view, Width = 1240, Height = 740, ShowInTaskbar = false };
        try
        {
            vm.EditingQueueIndex = 0; vm.ReplaceQueue([a, b, c]);
            vm.EditingQueueIndex = 1; vm.ReplaceQueue([d]); vm.QueueLoopEnabled = true; vm.QueuePlaylistPath = Path.Combine(directory, "Paus.fdjplaylist.json");
            vm.EditingQueueIndex = 0; window.Show(); await Idle();
            var list = (ListBox)view.FindName("QueueList"); list.SelectedIndex = 0;
            void Remove() => typeof(AutoplayView).GetMethod("RemoveSelected_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            Remove(); Check(list.SelectedItem == b, "Delete selects the row beneath");
            Remove(); Check(list.SelectedItem == c, "Repeated delete continues without reselecting");
            Remove(); Check(list.SelectedIndex == -1 && list.Items.Count == 0, "Last row deletion leaves an empty valid selection");
            vm.ReplaceQueue([a, b]); list.SelectedIndex = 1; Remove();
            Check(list.SelectedItem == a, "Deleting the final row selects the preceding remaining row");
            vm.ReplaceQueue([a, b]);
            Check(!await view.NewPlaylistAsync() && vm.LeftPlaybackQueue.Count == 2, "Cancel preserves the selected playlist");
            choice = UnsavedProfileChoice.Save;
            Check(!await view.NewPlaylistAsync() && saves == 1 && vm.LeftPlaybackQueue.Count == 2, "Canceling or failing Save aborts replacement");
            saved = true;
            Check(await view.NewPlaylistAsync() && saves == 2 && vm.LeftPlaybackQueue.Count == 0 && vm.RightPlaybackQueue.Single() == d,
                "Save then New replaces only the selected list");
            choice = UnsavedProfileChoice.Cancel;
            Check(await view.NewPlaylistAsync() && vm.LeftPlaybackQueue.Count == 0, "An empty list is replaced directly without a save prompt");
            Check(prompts == 3, "Only replacements of populated lists prompt");
            choice = UnsavedProfileChoice.Discard; vm.ReplaceQueue([a, b]);
            Check(await view.ClearPlaylistAsync() && vm.LeftPlaybackQueue.Count == 0 && vm.RightPlaybackQueue.Single() == d, "Clear is scoped to the selected list");
            vm.ReplaceQueue([a, b]); vm.QueueLoopEnabled = false;
            var rightFile = Path.Combine(directory, "Paus.fdjplaylist.json");
            await PlaylistService.Shared.SaveAsync(rightFile, new(2, true, true, [new(d.Title, d.FilePath)]));
            vm.PlayQueuedItem(a); vm.EditingQueueIndex = 1;
            Check(await view.LoadPlaylistAsync(rightFile, false) && vm.ActiveQueueItem == a && vm.LeftPlaybackQueue.SequenceEqual(new[] { a, b }) && vm.RightPlaybackQueue.Single().FilePath == d.FilePath,
                "Loading the right list preserves left playback and content");
            vm.EditingQueueIndex = 0; audio.StopAll();
            var path = Path.Combine(directory, "Dual.floorballdj.json"); await vm.SaveAsync(path);
            vm.EditingQueueIndex = 1; vm.ReplaceQueue([]); await vm.LoadAsync(path);
            Check(vm.LeftPlaybackQueue.Select(item => item.Title).SequenceEqual(new[] { a.Title, b.Title }) && vm.RightPlaybackQueue.Single().Title == d.Title,
                "Both complete queues round-trip through the profile");
            vm.EditingQueueIndex = 1;
            Check(vm.QueueLoopEnabled && vm.QueuePlaylistPath?.EndsWith("Paus.fdjplaylist.json") == true, "Second list flags and file name round-trip");
            await Idle();
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render((Visual)window.Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "autoplay-dual-playlists.png")); encoder.Save(stream);
            }
            // Real virtualization/recycling must retain one-based row numbers,
            // including after counts change. MaxValue alternation overflows here.
            vm.EditingQueueIndex = 0;
            vm.ReplaceQueue(Enumerable.Range(0, 300).Select(index => new Jingle { Title = $"Rad {index + 1}", FilePath = a.FilePath }));
            vm.EditingQueueIndex = 1;
            vm.ReplaceQueue(Enumerable.Range(0, 180).Select(index => new Jingle { Title = $"Höger {index + 1}", FilePath = a.FilePath }));
            async Task CheckNumbers(ListBox queue)
            {
                await Idle();
                var realized = Children(queue).OfType<ListBoxItem>().ToArray();
                Check(realized.Length > 0, "Numbering fixture realizes rows");
                foreach (var container in realized)
                {
                    var index = queue.ItemContainerGenerator.IndexFromContainer(container);
                    if (index < 0) continue;
                    Check(QueueRowNumbers.GetPosition(container) == index + 1,
                        $"Visible row {index + 1} has the exact positive playlist position");
                }
            }
            var right = (ListBox)view.FindName("RightQueueList");
            foreach (var queue in new[] { list, right })
            {
                foreach (var fraction in new[] { 0d, 1d, .5, 0d, .9, .1 })
                {
                    queue.ScrollIntoView(queue.Items[(int)((queue.Items.Count - 1) * fraction)]);
                    await CheckNumbers(queue);
                }
            }
            vm.EditingQueueIndex = 0;
            vm.MoveQueueItem(vm.PlaybackQueue[2], 90);
            vm.RemoveFromQueue(vm.PlaybackQueue[0]);
            vm.AddToQueue(new Jingle { Title = "Ny rad", FilePath = b.FilePath });
            list.ScrollIntoView(list.Items[0]); await CheckNumbers(list);
            list.ScrollIntoView(list.Items[^1]); await CheckNumbers(list);
            await CheckNumbers(right);
        }
        finally { window.Close(); vm.SetAutoplayMode(false); }
        Console.WriteLine("PASS: two independent Autoplay sources, natural completion/crossfade and resume isolation, repeated deletion, save/cancel/clear, and profile round-trip.");
    }

    private static Jingle Song(string root, string name, double seconds)
    {
        var path = Path.Combine(root, name + ".wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) { var bytes = new byte[(int)(192000 * seconds)]; writer.Write(bytes, 0, bytes.Length); }
        return new Jingle { Title = name, FilePath = path, DurationSeconds = seconds };
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
    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(condition(), message);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
