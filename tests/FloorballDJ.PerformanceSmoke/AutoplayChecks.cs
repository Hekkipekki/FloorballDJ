using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class AutoplayChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        var directory = Path.Combine(root, "autoplay");
        Directory.CreateDirectory(directory);
        var capture = Path.Combine(directory, "capture");
        var session = PerformanceDiagnostics.Start(capture);
        var uiThread = Environment.CurrentManagedThreadId;
        try
        {
            await CheckCache(directory);
            await CheckPlaylistSemantics(directory);
            await CheckCancellation(directory);
            await CheckHotkeyWithoutScan(directory);
            await CheckPrefetch(directory);
        }
        finally { await session.DisposeAsync(); }
        var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(item => item.GetProperty("type").GetString() == "event").ToArray();
        foreach (var stage in new[] { "PlaylistRead", "PlaylistParse", "PlaylistValidation", "AutoplayLibraryScan", "QueuePrefetchRead" })
        {
            var observations = events.Where(item => item.GetProperty("stage").GetString() == stage).ToArray();
            Check(observations.Length > 0 && observations.All(item => item.GetProperty("threadId").GetInt32() != uiThread), stage + " worker ownership");
        }
        Check(events.Where(item => item.GetProperty("stage").GetString() == "PlaylistQueueApply")
            .All(item => item.GetProperty("threadId").GetInt32() == uiThread), "Live queue is applied on the UI thread");
        Check(session.DroppedEvents == 0 && session.Failure is null, "Capture is complete");
        Check(!File.ReadAllText(Path.Combine(capture, "events.jsonl")).Contains(directory), "No private playlist/media paths in capture");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            foreach (var file in new[] { "session.json", "events.jsonl" }) File.Copy(Path.Combine(capture, file), Path.Combine(output, file), true);
            File.WriteAllText(Path.Combine(output, "autoplay-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, diagnosticApplicationStartup = "resources-only-v1", passed = true,
                measurement = "Real managed autoplay hotkey starts silent WASAPI playback before a blocked full-library scan is released. Not physical input or speaker onset.",
                playlistCacheEntryLimit = 32, playlistCacheEstimatedByteLimit = 4 * 1024 * 1024,
                playlistCacheLifetimeSeconds = 30, nextItemPrefixByteLimit = MediaPrefixPrefetcher.MaximumBytes,
                lostEvents = session.DroppedEvents
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: autoplay hotkey independent of full-library scan, playlist format/order/identity/clip/gain parity, bounded fresh cache, stale load rejection and bounded next-file prefix hint.");
    }

    private static async Task CheckCache(string root)
    {
        var media = Wave(root, "cache.wav");
        var path = Write(root, "cache.json", false, true, [new("one", media), new("two", media)]);
        var reads = 0;
        var service = new PlaylistService(readText: file => { reads++; return File.ReadAllText(file); });
        var first = await service.PrepareAsync(path, null);
        var second = await service.PrepareAsync(path, null);
        Check(reads == 1 && first!.ExistingEntries.Length == 2 && second!.ExistingEntries.Length == 2, "Cache preserves duplicates and avoids repeated parse/read");
        File.Delete(media);
        Check((await service.PrepareAsync(path, null))!.ExistingEntries.Length == 0 && reads == 1, "Media is revalidated on every cache hit");
        Wave(root, "cache.wav");
        Check((await service.PrepareAsync(path, null))!.ExistingEntries.Length == 2, "Reappearing media is admitted");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await service.PrepareAsync(path, null);
        Check(reads == 2, "Modified timestamp invalidates parsed data");
        File.AppendAllText(path, " ");
        await service.PrepareAsync(path, null);
        Check(reads == 3, "Changed length invalidates parsed data");
        await service.PrepareAsync(path, null, forceFresh: true);
        Check(reads == 4, "Manual load forces a fresh read");
        var document = new PlaylistDocument(2, true, false, [new("replacement", media)]);
        var save = service.SaveAsync(path, document);
        document.Entries!.Clear();
        await save;
        var expected = JsonSerializer.Serialize(new PlaylistDocument(2, true, false, [new("replacement", media)]), new JsonSerializerOptions { WriteIndented = true });
        Check(File.ReadAllText(path) == expected, "Save captures owned data and retains RC1 JSON bytes");
        var replaced = await service.PrepareAsync(path, null);
        Check(reads == 5 && replaced!.Definition.ShuffleEnabled && !replaced.Definition.LoopEnabled && replaced.ExistingEntries[0].Title == "replacement", "Save invalidates the cache");
        File.Delete(path);
        Check(await service.PrepareAsync(path, null, allowMissing: true) is null && service.CacheCount == 0, "Missing default removes stale cache");
        File.WriteAllText(path, "broken");
        try { await service.PrepareAsync(path, null); throw new InvalidOperationException("Expected malformed JSON rejection"); }
        catch (JsonException) { }
        Check(service.CacheCount == 0, "Parse failure is not cached");
        var lru = new PlaylistService(capacity: 2);
        for (var index = 0; index < 3; index++) await lru.PrepareAsync(Write(root, $"lru-{index}.json", false, true, [new("one", media)]), null);
        Check(lru.CacheCount == 2, "Cache entry bound");
        var tiny = new PlaylistService(byteLimit: 1);
        await tiny.PrepareAsync(Write(root, "large.json", false, true, [new("one", media)]), null);
        Check(tiny.CacheCount == 0 && tiny.CacheBytes == 0, "Oversize definitions still load without retention");
        var payloadBudget = 256L + 128L + 2L * ("one".Length + media.Length) + 1;
        var bytesBounded = new PlaylistService(byteLimit: payloadBudget);
        await bytesBounded.PrepareAsync(Path.Combine(root, "lru-0.json"), null);
        await bytesBounded.PrepareAsync(Path.Combine(root, "lru-1.json"), null);
        Check(bytesBounded.CacheCount == 1 && bytesBounded.CacheBytes <= payloadBudget, "Aggregate byte budget evicts otherwise individually cacheable definitions");
        var expiredReads = 0;
        var expired = new PlaylistService(lifetime: TimeSpan.Zero, readText: file => { expiredReads++; return File.ReadAllText(file); });
        await expired.PrepareAsync(Path.Combine(root, "large.json"), null);
        await expired.PrepareAsync(Path.Combine(root, "large.json"), null);
        Check(expiredReads == 2, "Expired definitions are read again");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var admittedReads = 0;
        var gated = new PlaylistService(readText: file =>
        {
            Interlocked.Increment(ref admittedReads); entered.TrySetResult();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "Admission fixture release"); return File.ReadAllText(file);
        });
        var active = gated.PrepareAsync(Path.Combine(root, "large.json"), null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var waitingCancellation = new CancellationTokenSource();
        try
        {
            var waiting = gated.PrepareAsync(Path.Combine(root, "large.json"), null, cancellationToken: waitingCancellation.Token);
            waitingCancellation.Cancel(); await Canceled(waiting);
            Check(admittedReads == 1 && !active.IsCompleted, "Canceled admission must not launch a second reader or wait for the first");
        }
        finally { release.Set(); }
        await active;
    }

    private static async Task CheckPlaylistSemantics(string root)
    {
        var folder = Path.Combine(root, "music");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        var deckPath = Wave(folder, "deck.wav");
        var folderPath = Wave(Path.Combine(folder, "nested"), "folder.wav");
        var outside = Wave(root, "outside.wav");
        var unsupported = Path.Combine(folder, "exists.txt");
        File.WriteAllText(unsupported, "not audio");
        using var vm = CreateVm(root);
        vm.Settings.MusicFolderPath = folder + Path.DirectorySeparatorChar;
        vm.Settings.DeckCount = 1;
        var first = vm.Decks[2].Jingles[0]; // A hidden deck still wins playlist matching.
        first.FilePath = deckPath;
        first.Title = "deck custom";
        first.StartSeconds = .1; first.EndSeconds = .5; first.GainDb = 3;
        first.PitchSemitones = 2; first.TempoPercent = 5; first.RatePercent = 4;
        first.FadeInOverrideSeconds = .2; first.PlayMode = JinglePlayMode.Mix;
        vm.Decks[3].Jingles[0].FilePath = deckPath;
        var view = new AutoplayView { DataContext = vm };
        var entries = new List<PlaylistEntry>
        {
            new("saved deck", deckPath.ToUpperInvariant()), new("duplicate deck", deckPath),
            new("saved folder", folderPath.ToUpperInvariant()), new("duplicate folder", folderPath),
            new("saved outside", outside), new("duplicate outside", outside), new("unsupported", unsupported),
            new("missing", Path.Combine(root, "missing.wav"))
        };
        var path = Write(root, "semantics.json", true, false, entries, version: 99);
        // Preparation without real playback lets us compare every queue reference.
        vm.SetAutoplayMode(true);
        await view.RefreshAvailableAsync();
        Check(await view.LoadPlaylistAsync(path, false, 30), "Unknown object version retains RC1 document handling");
        Check(vm.PlaybackQueue.Count == 7 && ReferenceEquals(vm.PlaybackQueue[0], first) && ReferenceEquals(vm.PlaybackQueue[1], first), "First matching hidden deck and duplicate identity are retained");
        Check(vm.PlaybackQueue[2].Title == "folder" && ReferenceEquals(vm.PlaybackQueue[2], vm.PlaybackQueue[3]), "Folder-only titles/duplicate identity");
        Check(vm.PlaybackQueue[4].Title == "saved outside" && !ReferenceEquals(vm.PlaybackQueue[4], vm.PlaybackQueue[5]), "Outside folder entries keep saved title and separate identities");
        Check(vm.PlaybackQueue[6].Title == "unsupported" && vm.QueueShuffleEnabled && !vm.QueueLoopEnabled, "Existing unsupported files are still admitted; v2 flags preserved");
        Check((double)Field(vm, "_playbackQueueGainOffsetDb")! == 12, "Queue volume clamp preserved");
        var available = (ListBox)view.FindName("AvailableList");
        var allDeckRaw = available.Items.Cast<Jingle>().Single(item => item.FilePath == folderPath);
        Check(allDeckRaw.StartSeconds == 0 && allDeckRaw.GainDb == 0 && allDeckRaw.PlayMode == JinglePlayMode.Solo, "All-files available filter uses raw-file defaults");
        var legacyPath = Path.Combine(root, "v1.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(entries));
        await view.LoadPlaylistAsync(legacyPath, false, -100);
        Check(!vm.QueueShuffleEnabled && vm.QueueLoopEnabled && (double)Field(vm, "_playbackQueueGainOffsetDb")! == -60, "v1 defaults/order/volume clamp retained");
        var queue = vm.PlaybackQueue.ToArray();
        Check(!await view.LoadDefaultPlaylistAndStartAsync(Path.Combine(root, "absent.json"), 0) && vm.PlaybackQueue.SequenceEqual(queue), "Missing default leaves queue and flags untouched");
        var empty = Write(root, "empty.json", true, false, []);
        Check(!await view.LoadPlaylistAsync(empty, false) && vm.PlaybackQueue.Count == 0 && vm.QueueShuffleEnabled && !vm.QueueLoopEnabled, "Valid empty playlist clears queue and applies flags");
        var nullPath = Path.Combine(root, "null.json"); File.WriteAllText(nullPath, "null");
        await view.LoadPlaylistAsync(nullPath, false);
        Check(vm.PlaybackQueue.Count == 0 && !vm.QueueShuffleEnabled && vm.QueueLoopEnabled, "Null document retains fallback defaults");
        File.WriteAllText(nullPath, "{\"Version\":2,\"ShuffleEnabled\":true,\"LoopEnabled\":false,\"Entries\":null}");
        await view.LoadPlaylistAsync(nullPath, false);
        Check(vm.PlaybackQueue.Count == 0 && vm.QueueShuffleEnabled && !vm.QueueLoopEnabled, "Null entry list retains document flags");
        File.WriteAllText(nullPath, "{\"entries\":[]}");
        await view.LoadPlaylistAsync(nullPath, false);
        Check(vm.PlaybackQueue.Count == 0 && !vm.QueueShuffleEnabled && !vm.QueueLoopEnabled, "Case-sensitive JSON property behavior remains unchanged");

        var service = new PlaylistService();
        var targeted = await service.PrepareAsync(path, folder + Path.DirectorySeparatorChar);
        Check(targeted!.FolderFiles.Count == 2 && targeted.FolderFiles[folderPath.ToUpperInvariant()] == folderPath && targeted.FolderFiles[deckPath] == deckPath, "Targeted ancestry preserves actual filename/path casing and nested/trailing-root membership");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, folder);
        var relativeFile = Path.Combine(relative, "nested", "folder.wav");
        var relativePlaylist = Write(root, "relative.json", false, true, [new("relative", relativeFile)]);
        Check((await service.PrepareAsync(relativePlaylist, relative))!.FolderFiles.ContainsKey(relativeFile), "Relative library enumeration spelling is preserved");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static async Task CheckCancellation(string root)
    {
        var media = Wave(root, "blocked.wav");
        var path = Write(root, "blocked.json", false, true, [new("new", media)]);
        foreach (var cancel in new[] { "latest", "profile", "unload", "stop", "leave", "queue", "folder" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var count = 0;
            var service = new PlaylistService(readText: file =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    entered.TrySetResult();
                    Check(release.Wait(TimeSpan.FromSeconds(15)), "Blocked reader release");
                }
                return File.ReadAllText(file);
            });
            using var vm = CreateVm(root);
            var existing = new Jingle { Title = "old", FilePath = media };
            vm.ReplaceQueue([existing]); vm.SetAutoplayMode(true);
            var view = new AutoplayView { DataContext = vm, Playlists = service };
            var pending = view.LoadPlaylistAsync(path, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var input = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => input = true, DispatcherPriority.Input);
            Check(input && !pending.IsCompleted, "UI input proceeds while the playlist reader is blocked");
            Task<bool>? newer = null;
            try
            {
                switch (cancel)
                {
                    case "latest": newer = view.LoadPlaylistAsync(Write(root, "newer.json", true, false, [new("latest", media)]), false); break;
                    case "profile":
                        var projects = new ProjectService();
                        var replacement = Path.Combine(root, "replacement.floorballdj.json");
                        await projects.SaveAsync(ProjectService.CreateDefault(), replacement);
                        await vm.LoadAsync(replacement);
                        break;
                    case "unload": view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                    case "stop": view.CancelPlaylistPreparation(); break;
                    case "leave": vm.SetAutoplayMode(false); vm.SetAutoplayMode(true); break;
                    case "queue": vm.ReplaceQueue([existing]); break;
                    case "folder": vm.Settings.MusicFolderPath = root; break;
                }
                release.Set();
                await Canceled(pending);
                if (newer is not null) Check(await newer && vm.PlaybackQueue.Single().Title == "latest" && vm.QueueShuffleEnabled, "Only newest request applies");
                else if (cancel != "profile") Check(vm.PlaybackQueue.Single() == existing, "Canceled preparation cannot replace the queue");
                else Check(vm.PlaybackQueue.Single() == existing, "Old request cannot change the queue after profile replacement");
            }
            finally { release.Set(); view.CancelPlaylistPreparation(); }
        }
        // Edit a live deck while immutable file preparation is pending.
        var editEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var editRelease = new ManualResetEventSlim();
        using var editVm = CreateVm(root);
        var deck = editVm.Decks[0].Jingles[0]; deck.FilePath = media;
        var editView = new AutoplayView { DataContext = editVm, Playlists = new PlaylistService(readText: file =>
        {
            editEntered.TrySetResult(); Check(editRelease.Wait(TimeSpan.FromSeconds(10)), "Edit release"); return File.ReadAllText(file);
        }) };
        var edit = editView.LoadPlaylistAsync(path, false);
        await editEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        deck.StartSeconds = .4; deck.GainDb = 7;
        editRelease.Set(); await edit;
        Check(ReferenceEquals(editVm.PlaybackQueue.Single(), deck) && editVm.PlaybackQueue[0].StartSeconds == .4 && editVm.PlaybackQueue[0].GainDb == 7, "Live clips/effects are resolved after preparation");
    }

    private static async Task CheckHotkeyWithoutScan(string root)
    {
        var folder = Path.Combine(root, "blocked-library"); Directory.CreateDirectory(folder);
        var media = Wave(folder, "hotkey.wav");
        var next = Wave(folder, "next.wav");
        var path = Write(root, "hotkey.json", false, true, [new("saved", media.ToUpperInvariant()), new("next", next.ToUpperInvariant()), new("duplicate", next)]);
        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        var view = (AutoplayView)window.FindName("EmbeddedAutoplay");
        using var release = new ManualResetEventSlim();
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(AutoplayView).GetProperty("LibraryEnumerator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view,
            (Func<string, HashSet<string>, CancellationToken, List<string>>)((_, _, token) =>
            {
                scanEntered.TrySetResult(); Check(release.Wait(TimeSpan.FromSeconds(15)), "Blocked scan release"); token.ThrowIfCancellationRequested(); return [media, next];
            }));
        vm.Settings.MusicFolderPath = folder;
        vm.Settings.MasterVolumeDb = -60; vm.Settings.FadeInSeconds = 0; vm.QueueTransitionSeconds = 0;
        vm.Settings.AutoplayProfiles = [new AutoplayProfile { PlaylistPath = path, Shortcut = "F9", VolumeDb = -9 }];
        using var source = new HwndSource(new HwndSourceParameters("autoplay fixture") { Width = 1, Height = 1, WindowStyle = 0 });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PrimaryPlaybackStarted += (_, _) => started.TrySetResult();
        try
        {
            // The production handler reads modifier state from the desktop, even
            // for synthetic input. Ordinary typing in another app can interfere.
            Keyboard.ClearFocus();
            var handled = false;
            for (var attempt = 0; attempt < 200 && !handled; attempt++)
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F9) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, args]);
                    handled = args.Handled;
                }
                if (!handled) await Task.Delay(50);
            }
            Check(handled, "Autoplay hotkey is handled after desktop modifier contention clears");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!release.IsSet && vm.ActiveQueueItem?.FilePath == media && vm.ActiveQueueItem.Title == "hotkey", "Actual hotkey starts before full scan can finish; folder file title preserved");
            Check(ReferenceEquals(vm.PlaybackQueue[1], vm.PlaybackQueue[2]), "Folder duplicate identity also preserved without available-library results");
            Check((double)Field(vm, "_playbackQueueGainOffsetDb")! == -9, "Profile gain preserved on startup");
            Check(vm.NextQueuePrefetchPath() == next, "Sequential hint predicts the next queue entry");
            var positions = vm.PlaybackQueue.Select(item => item.SessionPlayCount).ToArray();
            vm.QueueShuffleEnabled = true;
            Check(vm.NextQueuePrefetchPath() == next && vm.PlaybackQueue.Select(item => item.SessionPlayCount).SequenceEqual(positions), "Shuffle hint does not advance queue/session state");
            release.Set();
            vm.QueueShuffleEnabled = false;
            Check(vm.PlayNextQueued() && vm.ActiveQueueItem?.FilePath == next, "Actual sequential advance retains the next item");
            await vm.Audio.StopAllDeClickedAsync();
        }
        finally
        {
            release.Set(); view.CancelPlaylistPreparation();
            typeof(MainWindow).GetField("_closeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }
    }

    private static async Task CheckPrefetch(string root)
    {
        var path = Path.Combine(root, "prefix.bin"); File.WriteAllBytes(path, new byte[1024 * 1024]);
        var service = new MediaPrefixPrefetcher();
        Check(await service.ReadAsync(path, CancellationToken.None) == MediaPrefixPrefetcher.MaximumBytes, "Prefetch byte cap");
        var small = Path.Combine(root, "small.bin"); File.WriteAllBytes(small, new byte[17]);
        Check(await service.ReadAsync(small, CancellationToken.None) == 17, "Short file prefix");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Canceled(service.ReadAsync(path, canceled.Token));
        using var vm = CreateVm(root);
        vm.ReplaceQueue([new Jingle { FilePath = path }]); vm.SetAutoplayMode(true); vm.QueueLoopEnabled = false;
        typeof(MainViewModel).GetField("_queuePlaybackIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, 1);
        Check(vm.NextQueuePrefetchPath() is null, "No prefetch beyond a non-looping queue");
        vm.QueueLoopEnabled = true;
        Check(vm.NextQueuePrefetchPath() == path, "Loop hint wraps without changing index");
    }

    private static MainViewModel CreateVm(string root)
    {
        var projects = new ProjectService();
        return new(projects, new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), new AudioEngine());
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static string Write(string root, string name, bool shuffle, bool loop, List<PlaylistEntry> entries, int version = 2)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new PlaylistDocument(version, shuffle, loop, entries)));
        return path;
    }
    private static string Wave(string root, string name)
    {
        var path = Path.Combine(root, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
        writer.Write(new byte[48000 * 4 * 10], 0, 48000 * 4 * 10);
        return path;
    }
    private static async Task Canceled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected canceled preparation");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
