using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;

internal static class SaveChecks
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static async Task RunAsync(string root, string? output = null)
    {
        CheckDetachedGraph();
        var capture = Path.Combine(root, "save-behavior");
        var session = PerformanceDiagnostics.Start(capture);
        var uiThread = Environment.CurrentManagedThreadId;
        object coalescing;
        try
        {
            await CheckOrderedWritesAndFailures(root);
            coalescing = await CheckAutosaveAndFlush(root, capture);
            await CheckSaveAsAndProfileChanges(root);
            await CheckAutosaveFailureAndDisposal(root, capture);
        }
        finally { await session.DisposeAsync(); }
        var events = ReadEvents(capture);
        var snapshots = events.Where(item => Stage(item) == "SaveSnapshot").ToArray();
        var serialization = events.Where(item => Stage(item) == "SaveSerialization").ToArray();
        Check(snapshots.Length > 0 && snapshots.All(item => item.GetProperty("threadId").GetInt32() == uiThread),
            "Snapshots must be captured on the calling model/UI thread");
        Check(serialization.Length > 0 && serialization.All(item => item.GetProperty("threadId").GetInt32() != uiThread),
            "JSON serialization must run off the UI thread");
        Check(session.DroppedEvents == 0 && session.Failure is null, "Save correctness capture must be complete");
        var timings = new[] { BenchmarkCapture(698), BenchmarkCapture(5000) };
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "save-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, optimization = "p1-owned-save-v1", checkedUtc = DateTimeOffset.UtcNow,
                diagnosticApplicationStartup = "resources-only-v1",
                measurement = "Same-thread JSON baseline versus owned graph capture, synthetic profiles; not song-start or total-save latency.",
                passed = true, coalescing, timings, snapshotsOnOwnerThread = snapshots.Length,
                serializationsOnWorker = serialization.Length
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: exact save JSON, all persisted fields and detached mutable references, ordered writes/revisions/failure recovery, coalesced autosaves, UI progress, close flush, Save As/profile/revision/XML ownership and disposal.");
    }

    private static void CheckDetachedGraph()
    {
        var project = Fixture(13);
        var notifications = 0;
        project.Settings.PropertyChanged += (_, _) => notifications++;
        project.Decks[0].PropertyChanged += (_, _) => notifications++;
        project.Decks[0].Jingles[0].PropertyChanged += (_, _) => notifications++;
        var reference = JsonSerializer.SerializeToUtf8Bytes(project, Options);
        var snapshot = ProjectSaveSnapshot.Capture(project);
        var copy = (FloorballProject)typeof(ProjectSaveSnapshot).GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(snapshot)!;
        Check(reference.SequenceEqual(snapshot.Serialize(Options)) && notifications == 0, "Capture must preserve exact JSON without live notifications or normalization");
        AssertDetached(project, copy);
        copy.Settings.TitleFontSize = 33;
        copy.Decks[0].Name = "copy-only";
        copy.Decks[0].Jingles[0].Title = "copy-only";
        Check(notifications == 0, "Copies must not retain live UI subscriptions");
        snapshot = ProjectSaveSnapshot.Capture(project);
        MutatePersistedGraph(project, 97);
        project.Decks[0].Jingles.Clear();
        project.Settings.RandomPoolSetups.Clear();
        Check(reference.SequenceEqual(snapshot.Serialize(Options)), "Worker copy must survive subsequent scalar/list/collection edits");
        foreach (var malformed in new[] {
            new FloorballProject { Settings = null!, Decks = null! },
            new FloorballProject { Settings = new AppSettings { AutoplayProfiles = null!, RandomPoolProfiles = [null!],
                RandomPoolSetups = [new RandomPoolSetup { Profiles = null! }], TeamDeckProfiles = [new TeamDeckProfile { Players = [null!] }] },
                Decks = [null!, new Deck { PageLayouts = [null!], Jingles = [null!] }] }
        })
            Check(JsonSerializer.SerializeToUtf8Bytes(malformed, Options).SequenceEqual(ProjectSaveSnapshot.Capture(malformed).Serialize(Options)),
                "Saving must retain previously serializable null values without new normalization");
    }

    private static void AssertDetached(object? original, object? copy)
    {
        if (original is null) { Check(copy is null, "Null parity"); return; }
        Check(copy is not null && copy.GetType() == original.GetType(), "Snapshot type parity");
        var type = original.GetType();
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum || original is Guid or decimal) return;
        Check(!type.IsValueType, "New non-scalar value types need a snapshot ownership review: " + type.Name);
        Check(!ReferenceEquals(original, copy), "Every persisted mutable reference must be detached: " + type.Name);
        if (original is IEnumerable sequence)
        {
            var left = sequence.Cast<object?>().ToArray(); var right = ((IEnumerable)copy!).Cast<object?>().ToArray();
            Check(left.Length == right.Length, "Collection length parity");
            for (var index = 0; index < left.Length; index++) AssertDetached(left[index], right[index]);
        }
        else foreach (var property in Persisted(type)) AssertDetached(property.GetValue(original), property.GetValue(copy));
    }

    private static IEnumerable<PropertyInfo> Persisted(Type type) => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.GetCustomAttribute<JsonIgnoreAttribute>() is null && property.GetIndexParameters().Length == 0);

    private static void MutatePersistedGraph(object value, int seed)
    {
        if (value is IEnumerable sequence && value is not string)
        { foreach (var item in sequence) if (item is not null && !item.GetType().IsValueType && item is not string) MutatePersistedGraph(item, seed); return; }
        foreach (var property in Persisted(value.GetType()))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? next = type == typeof(string) ? "Unicode åäö \"quoted\" \\ path " + seed :
                type == typeof(Guid) ? Guid.NewGuid() : type == typeof(int) ? seed % 5 + 1 :
                type == typeof(long) ? (long)seed * 100 : type == typeof(double) ? seed * .125 :
                type == typeof(bool) ? seed % 2 == 0 : type.IsEnum ? Enum.GetValues(type).GetValue(seed % Enum.GetValues(type).Length) : null;
            if (next is not null && property.CanWrite) property.SetValue(value, next);
            else if (property.GetValue(value) is { } nested) MutatePersistedGraph(nested, seed);
        }
    }

    private static FloorballProject Fixture(int slots)
    {
        var project = new FloorballProject();
        project.Settings.AutoplayProfiles = [new()];
        project.Settings.RandomPoolDeckIds = [Guid.NewGuid()];
        project.Settings.RandomPoolJingleIds = [Guid.NewGuid()];
        project.Settings.RandomPoolProfiles = [new() { DeckIds = [Guid.NewGuid()], JingleIds = [Guid.NewGuid()], FollowUpJingleIds = [Guid.NewGuid()] }];
        project.Settings.RandomPoolSetups = [new() { Profiles = [new() { DeckIds = [Guid.NewGuid()], JingleIds = [Guid.NewGuid()], FollowUpJingleIds = [Guid.NewGuid()] }] }];
        project.Settings.TeamDeckProfiles = [new() { Players = [new()] }];
        for (var index = 0; index < slots; index++)
        {
            if (index % 47 == 0) project.Decks.Add(new Deck { PageCount = 2, Rows = 4, Columns = 12,
                PageLayouts = [new() { Rows = 4, Columns = 12 }, new() { Rows = 3, Columns = 9 }] });
            project.Decks[^1].Jingles.Add(new Jingle());
        }
        MutatePersistedGraph(project, 7);
        return project;
    }

    private static async Task CheckOrderedWritesAndFailures(string root)
    {
        var projects = new ProjectService();
        var path = Path.Combine(root, "ordered-save.floorballdj.json");
        var project = Fixture(1000);
        project.Name = "older-large";
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(project, Options);
        var first = projects.SaveAsync(project, path);
        project.Name = "newer-small"; project.Decks.Clear();
        var newBytes = JsonSerializer.SerializeToUtf8Bytes(project, Options);
        var second = new ProjectService().SaveAsync(project, path);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
        Check(File.ReadAllBytes(path).SequenceEqual(newBytes), "A later smaller save must never be overwritten by the older larger save");
        var revisions = await projects.GetRevisionsAsync(path);
        Check(revisions.Count == 1 && File.ReadAllBytes(revisions[0].Path).SequenceEqual(oldBytes), "Revision must preserve the exact prior snapshot");
        await projects.SaveAsync(project, path);
        Check((await projects.GetRevisionsAsync(path)).Count == 1, "Identical saves must not create a revision");
        var obstacle = Path.Combine(root, "save-directory-obstacle");
        File.WriteAllText(obstacle, "keep");
        var bad = projects.SaveAsync(project, Path.Combine(obstacle, "profile.json"));
        project.Name = "recovered";
        var recovery = projects.SaveAsync(project, path);
        await ExpectFailure(bad); await recovery;
        project.Settings.MasterVolumeDb = double.NaN;
        var badJson = projects.SaveAsync(project, path);
        project.Settings.MasterVolumeDb = -27;
        var goodJson = projects.SaveAsync(project, path);
        await ExpectFailure(badJson); await goodJson;
        Check((await projects.LoadAsync(path)).Name == "recovered" && File.ReadAllText(obstacle) == "keep", "Worker errors must not poison the save order or overwrite unrelated files");
        Check(!Directory.EnumerateFiles(root, ".*.tmp", SearchOption.TopDirectoryOnly).Any(), "Failed/completed saves must clean temporary output");
    }

    private static async Task<object> CheckAutosaveAndFlush(string root, string capture)
    {
        var projects = new ProjectService();
        using var vm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine());
        await DrainEvents(capture);
        var start = Count(capture, "SaveCompleted");
        var ready = Count(capture, "SaveSnapshotReady");
        var superseded = Count(capture, "SaveSuperseded");
        using (var blocker = new Semaphore(1, 1, @"Local\FloorballDJ.ProjectSave"))
        {
            Check(blocker.WaitOne(0), "Fixture needs the save semaphore");
            try
            {
                vm.Project.Name = "autosave-0";
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try { vm.RequestSave(); }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
                ready++;
                await WaitFor(() => Count(capture, "SaveSnapshotReady") >= ready);
                for (var index = 1; index <= 3; index++)
                {
                    vm.Project.Name = "autosave-" + index; vm.RequestSave(); ready++;
                    await WaitFor(() => Count(capture, "SaveSnapshotReady") >= ready);
                }
                var ran = false;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => ran = true, DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(2));
                await DrainEvents(capture);
                Check(ran && Count(capture, "SaveCompleted") == start, "Input-priority work must run while disk commit is blocked");
                Check(Count(capture, "SaveSuperseded") >= superseded + 2, "Replace intermediate pending autosaves after capture");
            }
            finally { blocker.Release(); }
        }
        await WaitFor(() => Count(capture, "SaveCompleted") >= start + 2);
        await GetAutosaveTask(vm);
        await DrainEvents(capture);
        Check((await projects.LoadAsync(vm.CurrentProjectPath)).Name == "autosave-3", "Autosave must commit the latest pending snapshot");
        Check(Count(capture, "SaveCompleted") == start + 2, "Four captured autosaves should commit only the active and latest versions");

        using (var blocker = new Semaphore(1, 1, @"Local\FloorballDJ.ProjectSave"))
        {
            Check(blocker.WaitOne(0), "Close fixture needs the save semaphore");
            Task flush;
            try
            {
                vm.Project.Name = "closing-1";
                flush = vm.FlushSavesAsync();
                vm.Project.Name = "closing-2"; vm.RequestSave();
                Check(!flush.IsCompleted, "Close must await its outstanding disk commit");
            }
            finally { blocker.Release(); }
            await flush.WaitAsync(TimeSpan.FromSeconds(8));
        }
        Check((await projects.LoadAsync(vm.CurrentProjectPath)).Name == "closing-2", "Close flush must include changes requested while the first flush was writing");
        return new { capturedAutosaves = 4, committedAutosaves = 2, supersededAutosaves = 2, inputProgressWhileWriteBlocked = true, closeIncludesLaterEdits = true };
    }

    private static async Task CheckSaveAsAndProfileChanges(string root)
    {
        var directory = Path.Combine(root, "profile-ownership"); Directory.CreateDirectory(directory);
        var projects = new ProjectService();
        var preferences = new ProfilePreferencesService(directory, projects.DefaultProjectPath);
        using var vm = new MainViewModel(projects, preferences, new AudioEngine());
        var firstPath = Path.Combine(directory, "first.floorballdj.json");
        var secondPath = Path.Combine(directory, "second.floorballdj.json");
        using (var blocker = new Semaphore(1, 1, @"Local\FloorballDJ.ProjectSave"))
        {
            Check(blocker.WaitOne(0), "Save As fixture needs the save semaphore");
            Task first, second;
            try
            {
                vm.Project.Name = "first-owned"; first = vm.SaveAsync(firstPath);
                vm.Project.Name = "second-owned"; second = vm.SaveAsync(secondPath);
            }
            finally { blocker.Release(); }
            await Task.WhenAll(first, second);
        }
        Check((await projects.LoadAsync(firstPath)).Name == "first-owned" && (await projects.LoadAsync(secondPath)).Name == "second-owned", "Save As paths and contents must remain captured across awaits");
        Check(preferences.GetRecentProfiles().Count == 1 && preferences.GetRecentProfiles()[0] == secondPath, "Old save completion must not update the new profile's history");
        vm.Project.Name = "second-last-edit"; vm.RequestSave();
        await vm.LoadAsync(firstPath);
        await Task.Delay(225);
        Check(vm.Project.Name == "first-owned" && (await projects.LoadAsync(secondPath)).Name == "second-last-edit", "Profile switch must flush edits to their original target and cancel stale debounce");
        vm.Project.Name = "first-last-edit"; vm.RequestSave();
        await vm.LoadAsync(firstPath);
        Check(vm.Project.Name == "first-last-edit", "Reloading current profile must retain just-flushed edits");
        var revision = (await projects.GetRevisionsAsync(firstPath)).First();
        await vm.RestoreRevisionAsync(revision.Path);
        Check((await projects.LoadAsync(firstPath)).Name == vm.Project.Name, "Restore must save the restored content to the captured active path");
        vm.Project.Name = "before-xml"; vm.RequestSave();
        var xml = Path.Combine(directory, "legacy.xml");
        File.WriteAllText(xml, "<JinglePlaylist><Rows>4</Rows><Cols>5</Cols><Tabs>1</Tabs></JinglePlaylist>");
        await vm.ImportLegacyXmlAsync(xml);
        Check((await projects.LoadAsync(firstPath)).Name == "before-xml" && vm.CurrentProjectPath == projects.DefaultProjectPath,
            "XML/new-project replacement must flush the old target before choosing default storage");
        await vm.FlushSavesAsync();
    }

    private static async Task CheckAutosaveFailureAndDisposal(string root, string capture)
    {
        var projects = new ProjectService();
        using (var vm = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine()))
        {
            var failing = Path.Combine(root, "autosave-obstacle"); File.WriteAllText(failing, "keep");
            await ExpectFailure(vm.SaveAsync(Path.Combine(failing, "profile.json")));
            vm.Project.Name = "retry-latest"; vm.RequestSave();
            await WaitFor(() => vm.Status.StartsWith(LanguageService.Translate("Autosparning misslyckades: "), StringComparison.Ordinal));
            var previous = vm.Project;
            await ExpectFailure(vm.LoadAsync(projects.DefaultProjectPath));
            Check(ReferenceEquals(vm.Project, previous), "Failed dirty-profile flush must preserve the current profile");
            await vm.SaveAsync(Path.Combine(root, "autosave-recovered.floorballdj.json"));
        }
        await DrainEvents(capture);
        var ready = Count(capture, "SaveSnapshotReady");
        var disposed = new MainViewModel(projects, new ProfilePreferencesService(root, projects.DefaultProjectPath), new AudioEngine());
        disposed.RequestSave(); disposed.Dispose();
        await Task.Delay(225);
        Check(Count(capture, "SaveSnapshotReady") == ready, "Disposal must cancel uncaptured autosaves");
    }

    private static object BenchmarkCapture(int slots)
    {
        var fixture = Fixture(slots);
        _ = JsonSerializer.SerializeToUtf8Bytes(fixture, Options);
        _ = ProjectSaveSnapshot.Capture(fixture);
        var oldTimes = new List<double>(); var newTimes = new List<double>();
        var oldBytes = new List<long>(); var newBytes = new List<long>();
        var oldCollections = new List<bool>(); var newCollections = new List<bool>();
        void Measure(bool original)
        {
            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
            var allocation = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
            object result = original ? JsonSerializer.SerializeToUtf8Bytes(fixture, Options) : ProjectSaveSnapshot.Capture(fixture);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
            (original ? oldTimes : newTimes).Add(elapsed); (original ? oldBytes : newBytes).Add(bytes);
            (original ? oldCollections : newCollections).Add(gen0 != GC.CollectionCount(0) || gen1 != GC.CollectionCount(1) || gen2 != GC.CollectionCount(2));
            GC.KeepAlive(result);
        }
        for (var index = 0; index < 101; index++) { Measure(index % 2 == 0); Measure(index % 2 != 0); }
        object Statistics(List<double> times, List<long> bytes, List<bool> collections)
        {
            var maximumWithoutCollectionChange = times.Where((_, index) => !collections[index]).DefaultIfEmpty(0).Max();
            times.Sort(); bytes.Sort();
            return new { count = times.Count, p50Ms = times[50], p95Ms = times[95], p99Ms = times[99], maximumMs = times[^1], medianAllocatedBytes = bytes[50],
                samplesWithCollectionCountChange = collections.Count(value => value), maximumWithoutCollectionChangeMs = maximumWithoutCollectionChange };
        }
        return new { slots, warmupsPerPath = 1, alternatingMeasurementOrder = true,
            baselineUiJson = Statistics(oldTimes, oldBytes, oldCollections), ownedUiCapture = Statistics(newTimes, newBytes, newCollections),
            jsonBytes = JsonSerializer.SerializeToUtf8Bytes(fixture, Options).Length };
    }

    private static Task GetAutosaveTask(MainViewModel vm) => (Task)typeof(MainViewModel).GetField("_autosaveTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static async Task DrainEvents(string capture)
    {
        var marker = Guid.NewGuid().ToString("N");
        PerformanceDiagnostics.BeginOperation("SaveCheckBarrier").Mark("SaveCheckBarrier", detail: marker);
        await WaitFor(() => ReadEvents(capture).Any(item => Stage(item) == "SaveCheckBarrier" && item.GetProperty("detail").GetString() == marker));
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected save failure was swallowed");
    }
    private static async Task WaitFor(Func<bool> ready)
    {
        var started = Stopwatch.StartNew();
        while (!ready())
        { if (started.Elapsed.TotalSeconds > 5) throw new TimeoutException("Save fixture did not reach expected state"); await Task.Delay(10); }
    }
    private static JsonElement[] ReadEvents(string capture)
    {
        using var stream = new FileStream(Path.Combine(capture, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream); var events = new List<JsonElement>();
        while (reader.ReadLine() is { } line) { try { events.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { } }
        return events.ToArray();
    }
    private static string? Stage(JsonElement item) => item.TryGetProperty("stage", out var stage) ? stage.GetString() : null;
    private static int Count(string capture, string stage) => ReadEvents(capture).Count(item => Stage(item) == stage);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
