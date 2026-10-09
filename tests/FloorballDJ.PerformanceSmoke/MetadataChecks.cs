using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class MetadataChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        var directory = Path.Combine(root, "metadata");
        Directory.CreateDirectory(directory);
        var capture = Path.Combine(directory, "capture");
        var session = PerformanceDiagnostics.Start(capture);
        var uiThread = Environment.CurrentManagedThreadId;
        object work;
        try
        {
            work = await CheckCacheAndOwnership(directory);
            await CheckAdmissionAndCancellation(directory);
            await CheckLibrary(directory);
            await CheckImports(directory);
            await CheckQueuedAndPartialImports(directory);
            await CheckRelinkOwnership(directory);
            await CheckXml(directory);
        }
        finally { await session.DisposeAsync(); }
        var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(item => item.GetProperty("type").GetString() == "event").ToArray();
        foreach (var stage in new[] { "MetadataDurationRead", "MetadataFileProbe", "LibraryInspection", "LibraryRelinkSearch" })
        {
            var observations = events.Where(item => item.GetProperty("stage").GetString() == stage).ToArray();
            Check(observations.Length > 0 && observations.All(item => item.GetProperty("threadId").GetInt32() != uiThread), stage + " must run on workers");
        }
        foreach (var stage in new[] { "AudioImportApplyBatch", "LibraryRelinkApplyBatch" })
        {
            var observations = events.Where(item => item.GetProperty("stage").GetString() == stage).ToArray();
            Check(observations.Length > 0 && observations.All(item => item.GetProperty("threadId").GetInt32() == uiThread), stage + " must run on the UI owner");
        }
        Check(session.DroppedEvents == 0 && session.Failure is null, "Metadata capture must be complete");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "metadata-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, optimization = "p1-metadata-v1", checkedUtc = DateTimeOffset.UtcNow,
                diagnosticApplicationStartup = "resources-only-v1", passed = true, work,
                measurement = "Deterministic work counts and blocked-reader UI progress, not laptop/song-start latency.",
                cacheEntryLimit = 256, cacheLifetimeSeconds = 30, applyBatchLimit = 16,
                readerThreadOwnership = "Created/disposed on the same worker; scalar results only.",
                lostEvents = session.DroppedEvents
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: metadata cache bounds/freshness/failures, worker reader ownership and cancellation, responsive imports with preserved layout/order, file-status/matching parity, stale edit/profile/close rejection and asynchronous XML parity.");
    }

    private static async Task<object> CheckCacheAndOwnership(string root)
    {
        var path = Wave(root, "duration.wav", .125);
        using (var reference = new AudioFileReader(path))
        {
            var real = await new AudioMetadataService().ReadAsync([path]);
            Check(real[0].DurationSeconds == reference.TotalTime.TotalSeconds, "Real WAV duration must equal the previous reader operation");
        }
        var calls = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        var service = new AudioMetadataService(readDuration: file =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Reader delegate must execute off the UI thread");
            Interlocked.Increment(ref calls);
            return new FileInfo(file).Length;
        });
        var mutablePaths = Enumerable.Repeat(path, 698).ToList();
        var pending = service.ReadAsync(mutablePaths);
        mutablePaths.Clear();
        var duplicateResults = await pending;
        Check(duplicateResults.Length == 698 && calls == 1 && duplicateResults.All(item => item.Exists),
            "Capture paths before yielding and read duplicate-file duration once");
        var firstLength = duplicateResults[0].DurationSeconds;
        File.AppendAllText(path, "new-version");
        var changed = await service.ReadAsync([path]);
        Check(calls == 2 && changed[0].DurationSeconds > firstLength, "Length changes invalidate cached metadata");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(3));
        await service.ReadAsync([path]);
        Check(calls == 3, "Timestamp changes invalidate cached metadata even at the same length");
        File.Delete(path);
        var missing = await service.ReadAsync([path]);
        Check(!missing[0].Exists && missing[0].DurationSeconds is null, "Removal must clear the cache and report current absence");
        File.WriteAllText(path, "replacement");
        await service.ReadAsync([path]);
        Check(calls == 4, "Recreated file must not reuse an earlier cached duration");

        var smallPaths = Enumerable.Range(0, 3).Select(index => Wave(root, $"lru-{index}.wav", .01)).ToArray();
        var lruReads = 0;
        var lru = new AudioMetadataService(capacity: 2, readDuration: _ => ++lruReads);
        await lru.ReadAsync(smallPaths);
        await lru.ReadAsync([smallPaths[0]]);
        Check(lruReads == 4, "Duration cache must evict beyond its entry limit");
        var expiryReads = 0;
        var expiry = new AudioMetadataService(lifetime: TimeSpan.Zero, readDuration: _ => ++expiryReads);
        await expiry.ReadAsync([path, path]);
        Check(expiryReads == 2, "Expired entries must be re-read");
        var failedReads = 0;
        var recovery = new AudioMetadataService(readDuration: _ => ++failedReads == 1 ? throw new IOException("decoder failure") : 4);
        var failure = await recovery.ReadAsync([path]);
        var recovered = await recovery.ReadAsync([path]);
        Check(failure[0].Exists && failure[0].DurationSeconds is null && recovered[0].DurationSeconds == 4 && failedReads == 2,
            "Reader errors preserve file admission, do not cache failure and can recover");
        var corrupt = Path.Combine(root, "corrupt.mp3");
        File.WriteAllText(corrupt, "not audio");
        var unreadable = await new AudioMetadataService().ReadAsync([corrupt]);
        Check(unreadable[0].Exists && unreadable[0].DurationSeconds is null, "Existing unsupported/corrupt file preserves duration fallback");
        return new { repeatedFileRequests = 698, durationReads = 1, invalidationChecks = 3, boundedCacheCapacity = 2, boundedCacheReads = lruReads };
    }

    private static async Task CheckAdmissionAndCancellation(string root)
    {
        var path = Wave(root, "blocked.wav", .01);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var active = 0;
        var maximum = 0;
        var reads = 0;
        var service = new AudioMetadataService(capacity: 0, readDuration: _ =>
        {
            var concurrent = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, concurrent);
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            try { Check(release.Wait(TimeSpan.FromSeconds(10)), "Blocked reader fixture timed out"); return 1; }
            finally { Interlocked.Decrement(ref active); }
        });
        using var firstCancellation = new CancellationTokenSource();
        var first = service.ReadAsync([path], firstCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var uiProgress = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => uiProgress = true, DispatcherPriority.Input);
            Check(uiProgress && !first.IsCompleted, "Input-priority work must run while the metadata reader is blocked");
            using var waitingCancellation = new CancellationTokenSource();
            var waiting = service.ReadAsync([path], waitingCancellation.Token);
            waitingCancellation.Cancel();
            await Canceled(waiting);
            firstCancellation.Cancel();
        }
        finally { release.Set(); }
        await Canceled(first);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ReadAsync([path])));
        Check(maximum == 1 && reads == 13, "One active reader per service; canceled queued work must not open a decoder");
    }

    private static async Task CheckLibrary(string root)
    {
        var existing = Wave(root, "library.wav", .01);
        var missing = Path.Combine(root, "missing-folder", "missing.mp3");
        AudioLibrarySource[] sources = [new("B", "B", existing), new("A", "A", existing), new("A", "Missing", missing)];
        var statistics = await AudioLibraryService.InspectAsync(sources);
        Check(statistics.Files.Length == 3 && statistics.Files.Count(item => !item.IsMissing) == 2 &&
              statistics.Bytes == new FileInfo(existing).Length && statistics.Formats == "MP3, WAV", "Per-cart counts, deduplicated bytes and formats must be preserved");
        Check(statistics.Files[0].IsMissing && statistics.Files[0].SuggestedRoot == root && statistics.Files[1].Title == "A",
            "Missing-first sorting and nearest existing folder guidance must be preserved");
        File.Delete(existing);
        var changed = await AudioLibraryService.InspectAsync(sources);
        Check(changed.Files.All(item => item.IsMissing) && changed.Bytes == 0, "Refresh must probe current file state without a stale status cache");

        var destination = Path.Combine(root, "matches");
        Directory.CreateDirectory(destination);
        var exact = Wave(destination, "Exact Song.wav", .01);
        var normalized = Wave(destination, "SpotifyDown Sång Anthem.wav", .01);
        var fuzzy = Wave(destination, "Bright Morning Piano.wav", .01);
        Wave(destination, "Ambiguous Song.wav", .01);
        Wave(destination, "Ambiguous Song.mp3", .01);
        AudioLibrarySource[] queries = [
            new("A", "ignored title", Path.Combine(root, "old", "Exact Song.wav")),
            new("A", "ignored title", Path.Combine(root, "old", "Sang Anthem.flac")),
            new("A", "Bright Morning Piano Demo", Path.Combine(root, "old", "unrelated123.flac")),
            new("A", "Ambiguous Song", Path.Combine(root, "old", "different123.flac")),
            new("A", "tiny", Path.Combine(root, "old", "x.flac")),
            new("A", "Bright Morning Piano Demo", exact)
        ];
        var metadata = new AudioMetadataService(readDuration: _ => 3);
        var matches = await AudioLibraryService.FindMatchesAsync(destination, queries, false, metadata);
        Check(matches.Select(item => item.SourceIndex).SequenceEqual([0, 1, 2]) && matches.Select(item => item.FilePath).SequenceEqual([exact, normalized, fuzzy]),
            "Exact/unique normalized/title/fuzzy matches, ambiguous/short rejection and valid-file skipping must be preserved");
        var updateAll = await AudioLibraryService.FindMatchesAsync(destination, [queries[5]], true, metadata);
        Check(updateAll.Length == 0, "Update-all must not replace a same-path exact match with a title-based alternative");
    }

    private static async Task CheckImports(string root)
    {
        var files = Enumerable.Range(0, 34).Select(index => Wave(root, $"import-{index:D2}.wav", .01)).ToArray();
        var window = new MainWindow { Metadata = new AudioMetadataService(readDuration: _ => 2) };
        var vm = (MainViewModel)window.DataContext;
        try
        {
            var deck = vm.Decks[0];
            ProjectService.ResizeDeckPageLayout(deck, 0, 1, 2);
            var kept = deck.Jingles[0];
            kept.FilePath = files[0]; kept.Title = "Keep me"; kept.StartSeconds = .004; kept.GainDb = -5;
            var text = deck.Jingles[1]; text.IsTextBlock = true; text.Title = "Keep header";
            var changesOnOwner = true;
            var owner = Environment.CurrentManagedThreadId;
            foreach (var item in deck.Jingles) item.PropertyChanged += (_, _) => changesOnOwner &= Environment.CurrentManagedThreadId == owner;
            var added = await window.AssignAudioFilesAsync(text, files.Concat([files[0], Path.Combine(root, "missing.wav"), Path.Combine(root, "ignored.txt")]));
            var imported = deck.Jingles.Where(item => item.HasAudio && !ReferenceEquals(item, kept)).ToArray();
            Check(added == 34 && imported.Select(item => item.FilePath).SequenceEqual(files) && imported.All(item => item.DurationSeconds == 2),
                "Import order, duplicates, missing/non-audio admission and duration must be preserved");
            Check(kept.Title == "Keep me" && kept.StartSeconds == .004 && kept.GainDb == -5 && text.IsTextBlock && text.Title == "Keep header" &&
                  deck.GetPageRows(0) == 18 && deck.PageCount == 1 && changesOnOwner, "Occupied/text slots, settings, row expansion and UI ownership must be preserved");
            ProjectService.ResizeDeckPageLayout(deck, 0, 50, 1);
            foreach (var item in deck.Jingles.Take(49)) { item.FilePath = files[0]; item.IsTextBlock = false; }
            var pageAdded = await window.AssignAudioFilesAsync(deck.Jingles[49], files.Take(3));
            Check(pageAdded == 3 && deck.PageCount == 2 && deck.ActivePage == 1, "Full first page must expand to a new page and select the imported page");
        }
        finally { Close(window); }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var delayed = new MainWindow { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            entered.TrySetResult();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "Import fixture timed out");
            return 2;
        }) };
        var delayedVm = (MainViewModel)delayed.DataContext;
        var original = delayedVm.Project;
        var task = delayed.AssignAudioFilesAsync(delayedVm.Decks[0].Jingles[0], [files[0]]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = Path.Combine(root, "replacement.floorballdj.json");
            var replacementProject = ProjectService.CreateDefault(); replacementProject.Name = "Replacement";
            await new ProjectService().SaveAsync(replacementProject, replacement);
            await delayedVm.LoadAsync(replacement);
            Check(original != delayedVm.Project, "Profile switch fixture must replace the graph");
        }
        finally { release.Set(); }
        Check(await task == 0 && delayedVm.Decks.All(deck => deck.Jingles.All(item => !item.HasAudio)), "Late import must not touch a replacement profile");
        Close(delayed);

        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closeRelease = new ManualResetEventSlim();
        var closing = new MainWindow { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            closeEntered.TrySetResult();
            Check(closeRelease.Wait(TimeSpan.FromSeconds(10)), "Close fixture timed out");
            return 2;
        }) };
        var closingVm = (MainViewModel)closing.DataContext;
        var canceledImport = closing.AssignAudioFilesAsync(closingVm.Decks[0].Jingles[0], [files[1]]);
        try { await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Close(closing); }
        finally { closeRelease.Set(); }
        Check(await canceledImport == 0 && !closingVm.Decks[0].Jingles[0].HasAudio, "Closed window must reject a late metadata result");
    }

    private static async Task CheckRelinkOwnership(string root)
    {
        var destination = Path.Combine(root, "relink"); Directory.CreateDirectory(destination);
        var files = Enumerable.Range(0, 34).Select(index => Wave(destination, $"relink-{index:D2}.wav", .01)).ToArray();
        var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine());
        var targets = vm.Decks.SelectMany(deck => deck.Jingles).Take(34).ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            targets[index].FilePath = Path.Combine(root, "moved", Path.GetFileName(files[index]));
            targets[index].Title = "Original " + index;
            targets[index].DurationSeconds = 9;
        }
        var window = new ManageAudioFilesWindow(vm, projects) { Metadata = new AudioMetadataService(readDuration: path => path == files[0] ? throw new IOException("unreadable") : 4) };
        try
        {
            var updated = await window.SearchAndUpdateAsync(destination, false);
            Check(updated == 34 && targets.Select(item => item.FilePath).SequenceEqual(files) && targets[0].DurationSeconds == 9 && targets.Skip(1).All(item => item.DurationSeconds == 4),
                "Relinking preserves match order and the previous duration when decoding fails");
            var displayed = ((ListBox)window.FindName("FilesList")).Items.Cast<AudioFileStatus>().ToArray();
            Check(displayed.Length == 34 && displayed.All(item => !item.IsMissing), "Refresh after relink must display current file status");
        }
        finally { window.Close(); }

        targets[0].FilePath = Path.Combine(root, "old", Path.GetFileName(files[0]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var edited = new ManageAudioFilesWindow(vm, projects) { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            entered.TrySetResult();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "Relink edit fixture timed out"); return 4;
        }) };
        var relink = edited.SearchAndUpdateAsync(destination, false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            targets[0].FilePath = "changed-by-user.wav";
        }
        finally { release.Set(); }
        Check(await relink == 0 && targets[0].FilePath == "changed-by-user.wav", "A newer path edit must win over stale relink preparation");
        edited.Close();

        targets[0].FilePath = Path.Combine(root, "old", Path.GetFileName(files[0]));
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closeRelease = new ManualResetEventSlim();
        var closing = new ManageAudioFilesWindow(vm, projects) { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            closeEntered.TrySetResult();
            Check(closeRelease.Wait(TimeSpan.FromSeconds(10)), "Relink close fixture timed out"); return 4;
        }) };
        var closeTask = closing.SearchAndUpdateAsync(destination, false);
        try { await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); closing.Close(); }
        finally { closeRelease.Set(); }
        Check(await closeTask == 0 && targets[0].IsMissing, "Closed relink window must not mutate the live model");

        for (var index = 0; index < targets.Length; index++)
            targets[index].FilePath = Path.Combine(root, "old", Path.GetFileName(files[index]));
        var partial = new ManageAudioFilesWindow(vm, projects) { Metadata = new AudioMetadataService(readDuration: _ => 4) };
        var closeQueued = false;
        void CloseAfterBatch(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Jingle.FilePath) || closeQueued) return;
            closeQueued = true;
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(partial.Close, DispatcherPriority.Input);
        }
        targets[0].PropertyChanged += CloseAfterBatch;
        try
        {
            Check(await partial.SearchAndUpdateAsync(destination, false) == 16 && targets.Take(16).Select(item => item.FilePath).SequenceEqual(files.Take(16)) &&
                  targets.Skip(16).All(item => item.IsMissing), "Close between relink batches must retain exactly the applied batch");
            await vm.FlushSavesAsync();
            var saved = await projects.LoadAsync(vm.CurrentProjectPath);
            Check(saved.Decks.SelectMany(deck => deck.Jingles).Count(item => files.Contains(item.FilePath)) == 16, "Partial relink changes must remain requested and flushable");
        }
        finally { targets[0].PropertyChanged -= CloseAfterBatch; }
    }

    private static async Task CheckQueuedAndPartialImports(string root)
    {
        var files = Enumerable.Range(0, 34).Select(index => Wave(root, $"queued-{index:D2}.wav", .01)).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var window = new MainWindow { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                Check(release.Wait(TimeSpan.FromSeconds(10)), "Queued import fixture timed out");
            }
            return 2;
        }) };
        var vm = (MainViewModel)window.DataContext;
        var first = window.AssignAudioFilesAsync(vm.Decks[0].Jingles[0], [files[0]]);
        Task<int> second;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = window.AssignAudioFilesAsync(vm.Decks[0].Jingles[0], [files[1]]);
            Check(!second.IsCompleted && reads == 1, "Explicit imports must wait in request order");
        }
        finally { release.Set(); }
        try
        {
            Check(await first == 1 && await second == 1 && vm.Decks[0].Jingles.Take(2).Select(item => item.FilePath).SequenceEqual(files.Take(2)),
                "Queued imports must use live empty slots without overwriting the earlier request");
        }
        finally { Close(window); }

        var partialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var partialRelease = new ManualResetEventSlim();
        var partialReads = 0;
        var partial = new MainWindow { Metadata = new AudioMetadataService(readDuration: _ =>
        {
            if (Interlocked.Increment(ref partialReads) == 17)
            {
                partialEntered.TrySetResult();
                Check(partialRelease.Wait(TimeSpan.FromSeconds(10)), "Partial import fixture timed out");
            }
            return 2;
        }) };
        var partialVm = (MainViewModel)partial.DataContext;
        var profilePath = Path.Combine(root, "partial-import.floorballdj.json");
        await partialVm.SaveAsync(profilePath);
        var import = partial.AssignAudioFilesAsync(partialVm.Decks[0].Jingles[0], files);
        try
        {
            await partialEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(partialVm.Decks[0].Jingles.Count(item => item.HasAudio) == 16, "First batch must be visible before later metadata completes");
            typeof(MainWindow).GetField("_closeInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(partial, true);
            await partialVm.FlushSavesAsync();
            Close(partial);
        }
        finally { partialRelease.Set(); }
        Check(await import == 16, "Closing must not apply the later prepared batch");
        var savedPartial = await new ProjectService().LoadAsync(profilePath);
        Check(savedPartial.Decks[0].Jingles.Where(item => item.HasAudio).Select(item => item.FilePath).SequenceEqual(files.Take(16)),
            "Closing flush must retain the already applied import batch");
    }

    private static async Task CheckXml(string root)
    {
        var path = Wave(root, "xml-duration.wav", .01);
        var xml = Path.Combine(root, "legacy.xml");
        File.WriteAllText(xml, $"<JinglePlaylist><Rows>1</Rows><Cols>2</Cols><Tabs>1</Tabs><JinglePanel><Name>Imported</Name><JingleMedia><GridPosition>0</GridPosition><Title>Clip</Title><FilePath>{System.Security.SecurityElement.Escape(path)}</FilePath><StartTime>1</StartTime><Loop>true</Loop><PlayMode>3</PlayMode></JingleMedia><JingleMedia><GridPosition>1</GridPosition><FilePath>missing.wav</FilePath><ClipDuration>00:00:12</ClipDuration></JingleMedia></JinglePanel></JinglePlaylist>");
        var synchronous = LegacyXmlImporter.Import(xml);
        var asynchronous = await LegacyXmlImporter.ImportAsync(xml);
        // Generated IDs differ; compare all persisted properties after assigning the same IDs.
        for (var index = 0; index < synchronous.Decks.Count; index++)
        {
            asynchronous.Decks[index].Id = synchronous.Decks[index].Id;
            for (var item = 0; item < synchronous.Decks[index].Jingles.Count; item++)
                asynchronous.Decks[index].Jingles[item].Id = synchronous.Decks[index].Jingles[item].Id;
        }
        asynchronous.Settings.RandomPoolSetups[0].Id = synchronous.Settings.RandomPoolSetups[0].Id;
        asynchronous.Settings.ActiveRandomPoolSetupId = synchronous.Settings.ActiveRandomPoolSetupId;
        Check(JsonSerializer.Serialize(synchronous) == JsonSerializer.Serialize(asynchronous), "Async XML import must preserve layout, colors, flags, clips, fallback duration and exact persisted values");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Canceled(LegacyXmlImporter.ImportAsync(xml, canceled.Token));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var metadata = new AudioMetadataService(readDuration: _ =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                Check(release.Wait(TimeSpan.FromSeconds(10)), "XML fixture timed out");
            }
            return .01;
        });
        var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine()) { Metadata = metadata };
        var secondXml = Path.Combine(root, "latest.xml"); File.Copy(xml, secondXml, true);
        var firstImport = vm.ImportLegacyXmlAsync(xml);
        Task<bool> latestImport;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            latestImport = vm.ImportLegacyXmlAsync(secondXml);
        }
        finally { release.Set(); }
        Check(!await firstImport && await latestImport && vm.Project.Name == "latest", "Latest XML request must win without an older result replacing it");

        var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var staleRelease = new ManualResetEventSlim();
        using var staleVm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine())
        {
            Metadata = new AudioMetadataService(readDuration: _ =>
            {
                staleEntered.TrySetResult();
                Check(staleRelease.Wait(TimeSpan.FromSeconds(10)), "Stale XML fixture timed out"); return .01;
            })
        };
        var staleImport = staleVm.ImportLegacyXmlAsync(xml);
        try
        {
            await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacementPath = Path.Combine(root, "xml-replacement.floorballdj.json");
            var replacement = ProjectService.CreateDefault(); replacement.Name = "Keep replacement";
            await projects.SaveAsync(replacement, replacementPath);
            await staleVm.LoadAsync(replacementPath);
        }
        finally { staleRelease.Set(); }
        Check(!await staleImport && staleVm.Project.Name == "Keep replacement", "XML completion must not replace a subsequently loaded profile");
    }

    private static string Wave(string root, string name, double seconds)
    {
        var path = Path.Combine(root, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
        writer.Write(new byte[(int)(seconds * 48000) * 4], 0, (int)(seconds * 48000) * 4);
        return path;
    }

    private static void Close(MainWindow window)
    {
        typeof(MainWindow).GetField("_closeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
    }

    private static async Task Canceled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
