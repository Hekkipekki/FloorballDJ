using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;

internal static class TransferAndDraftChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var previousData = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var previousLanguage = LanguageService.CurrentLanguage;
        var previousMain = Application.Current.MainWindow;
        root = Path.Combine(root, "transfer-draft"); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", root);
        try
        {
            await CheckDrafts(root);
            await CheckTransfers(root);
            await CheckCloseAndDialogs(root, output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previousData);
            LanguageService.SetLanguage(previousLanguage);
            Application.Current.MainWindow = previousMain;
        }
        Console.WriteLine("PASS: isolated working copies, manual save/failure/discard/recovery, session-only playback, portable queue/playlists, package and sync relocation, immutable versions, media reuse, incomplete-sync rejection and real close choices.");
    }

    private static async Task CheckDrafts(string root)
    {
        var projects = new ProjectService();
        var preferences = new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath);
        var path = Path.Combine(root, "Dalen.floorballdj.json");
        var original = ProjectService.CreateDefault(); original.Name = "Dalen";
        await projects.SaveAsync(original, path);
        var originalBytes = await File.ReadAllBytesAsync(path);
        WorkingCopyInfo recovery;
        using (var vm = new MainViewModel(projects, preferences, new AudioEngine()))
        {
            await vm.LoadAsync(path);
            Check(!vm.HasUnsavedChanges, "Opening a saved profile is clean");
            vm.Decks[0].Jingles[0].SessionPlayCount = 7;
            Check(!vm.HasUnsavedChanges, "Session history never dirties the profile");
            vm.Decks[0].Jingles[0].Title = "Osparad låt";
            vm.AddToQueue(new Jingle { Title = "Kö", FilePath = Path.Combine(root, "queue.wav") });
            vm.QueueShuffleEnabled = true; vm.QueueLoopEnabled = false;
            vm.RequestSave(); await vm.FlushSavesAsync();
            Check(vm.HasUnsavedChanges && File.Exists(vm.WorkingCopyPath), "Autosave remains dirty and creates a working copy");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(originalBytes), "Autosave must never overwrite a named profile");
            recovery = vm.RecoverableCopies.Single(copy => copy.SourcePath == path);
            var current = vm.CurrentProjectPath;
            var obstacle = Path.Combine(root, "save-obstacle"); await File.WriteAllTextAsync(obstacle, "keep");
            await ExpectFailure(vm.SaveAsync(Path.Combine(obstacle, "bad.floorballdj.json")));
            Check(vm.CurrentProjectPath == current && File.Exists(vm.WorkingCopyPath) && vm.HasUnsavedChanges, "Failed Save As retains identity and recovery data");
        }
        using (var vm = new MainViewModel(projects, preferences, new AudioEngine()))
        {
            await vm.RecoverAsync(recovery);
            Check(vm.CurrentProjectPath == path && vm.HasUnsavedChanges && vm.Decks[0].Jingles[0].Title == "Osparad låt", "Recovery restores edits under original identity");
            Check(vm.PlaybackQueue.Single().Title == "Kö" && vm.QueueShuffleEnabled && !vm.QueueLoopEnabled, "Unsaved queue and flags survive recovery");
            Check(vm.Decks.SelectMany(deck => deck.Jingles).All(item => item.SessionPlayCount == 0), "Recovery starts a fresh playback session");
            await vm.SaveAsync();
            Check(!vm.HasUnsavedChanges && !File.Exists(vm.WorkingCopyPath) && !vm.RecoverableCopies.Any(copy => copy.SourcePath == path), "Successful manual save removes the draft");
            var savedBytes = await File.ReadAllBytesAsync(path);
            vm.Decks[0].Jingles[0].Title = "Kasta detta"; vm.RequestSave();
            await vm.FlushSavesAsync(); await vm.DiscardChangesAsync();
            Check(!File.Exists(vm.WorkingCopyPath) && (await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes), "Don't save deletes draft and leaves saved profile unchanged");
            var media = Path.Combine(root, "relative.wav"); await File.WriteAllBytesAsync(media, [1, 2, 3]);
            vm.Decks[0].Jingles[0].FilePath = media; await vm.SaveAsync();
            vm.Decks[0].Jingles[0].Title = "Revision"; await vm.SaveAsync();
            var revision = (await vm.GetRevisionsAsync()).First();
            await vm.RestoreRevisionAsync(revision.Path);
            Check(vm.HasUnsavedChanges && vm.Decks[0].Jingles[0].FilePath == media, "Revision remains an unsaved edit and relative media uses original profile directory");
            await vm.DiscardChangesAsync();
        }
    }

    private static async Task CheckTransfers(string root)
    {
        var projects = new ProjectService();
        var source = Path.Combine(root, "home-C"); Directory.CreateDirectory(source);
        var media = new[] { "deck.mp3", "playlist-only.mp3", "queue-only.mp3" }.Select(name => Path.Combine(source, name)).ToArray();
        for (var i = 0; i < media.Length; i++) await File.WriteAllBytesAsync(media[i], [(byte)(i + 1), 21, 33, 44]);
        var playlist = Path.Combine(source, "Uppvärmning.fdjplaylist.json");
        await PlaylistService.Shared.SaveAsync(playlist, new(2, true, false, [new("Extra", media[1]), new("Deck", media[0]), new("Extra igen", media[1])]));
        var project = ProjectService.CreateDefault(); project.Name = "Dalen";
        project.Decks[0].Name = "Autoplay";
        project.Decks[0].Jingles[0].Title = "Mål"; project.Decks[0].Jingles[0].FilePath = media[0];
        var setupId = project.Settings.RandomPoolSetups[0].Id;
        project.Settings.AutoplayProfiles = [new() { Name = "Uppvärmning", PlaylistPath = playlist, Shortcut = "F8", VolumeDb = -3, RandomPoolSetupId = setupId }];
        project.Settings.AutoplayLastPlaylistPath = playlist;
        project.Settings.AutoplayQueueItems = [new() { Title = "Osparad kö", FilePath = media[2], GainDb = -2 }];
        project.Settings.AutoplayQueueShuffleEnabled = true; project.Settings.AutoplayQueueLoopEnabled = false;
        var package = Path.Combine(root, "Dalen.fdjbackup");
        await projects.CreateTransferPackageAsync(project, package, "Dalen");
        var sync = Path.Combine(root, "Drive-G");
        var first = await projects.SaveToSyncFolderAsync(project, sync, "Dalen");
        var sharedFiles = Directory.GetFiles(Path.Combine(sync, "Media"));
        var times = sharedFiles.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        project.Decks[0].Jingles[0].Title = "Ny målknapp";
        var second = await projects.SaveToSyncFolderAsync(project, sync, "Dalen");
        Check(projects.GetTransferVersions(sync).Count == 2 && first.Manifest.ProfileId == second.Manifest.ProfileId, "Both versions retain the stable profile identity");
        Check(sharedFiles.Length == 3 && sharedFiles.All(path => File.GetLastWriteTimeUtc(path) == times[path]) && Directory.GetFiles(Path.Combine(sync, "Media")).Length == 3,
            "Unchanged media bytes reuse identical shared files without rewriting");
        var latestBytes = await File.ReadAllBytesAsync(Path.Combine(sync, "floorballdj-backup.json"));
        var oldPackage = await File.ReadAllBytesAsync(package);
        project.Settings.AutoplayQueueItems.Add(new Jingle { FilePath = Path.Combine(source, "missing.mp3") });
        await ExpectFailure(projects.SaveToSyncFolderAsync(project, sync, "Dalen"));
        await ExpectFailure(projects.CreateTransferPackageAsync(project, package, "Dalen"));
        Check((await File.ReadAllBytesAsync(Path.Combine(sync, "floorballdj-backup.json"))).SequenceEqual(latestBytes) && (await File.ReadAllBytesAsync(package)).SequenceEqual(oldPackage), "Incomplete exports never replace a completed version or package");

        // Simulate different roots and removal of the home computer's files. No original path can work now.
        foreach (var file in Directory.GetFiles(source)) File.Delete(file);
        var relocated = Path.Combine(root, "USB-H");
        Directory.Move(sync, relocated);
        var versions = projects.GetTransferVersions(relocated);
        var restored = await projects.RestoreTransferVersionAsync(relocated, versions.Single(version => version.Manifest.ProfileFile == second.Manifest.ProfileFile));
        var loaded = await projects.LoadAsync(restored.ProfilePath);
        Check(loaded.Settings.AutoplayProfiles.Single().RandomPoolSetupId == setupId && loaded.Settings.RandomPoolSetups.Any(setup => setup.Id == setupId),
            "Sync transfer retains the Autoplay setup link");
        Check(loaded.Id == project.Id && loaded.Name == "Dalen" && loaded.Decks[0].Jingles[0].Title == "Ny målknapp", "Relocated profile preserves name, identity and selected version");
        Check(restored.MissingMediaCount == 0 && loaded.Settings.AutoplayQueueItems.Single().GainDb == -2 && File.Exists(loaded.Settings.AutoplayQueueItems.Single().FilePath), "Queue-only media and queue settings survive relocation");
        var prepared = await PlaylistService.Shared.PrepareAsync(loaded.Settings.AutoplayProfiles.Single().PlaylistPath!, null);
        Check(prepared!.ExistingEntries.Length == 3 && prepared.Definition.ShuffleEnabled && !prepared.Definition.LoopEnabled && prepared.ExistingEntries[0].Title == "Extra", "Playlist-only audio, duplicate order and flags survive");
        var profileJson = await File.ReadAllTextAsync(restored.ProfilePath);
        Check(!profileJson.Contains(source, StringComparison.OrdinalIgnoreCase) && !profileJson.Contains(root.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "Persisted portable profile has relative paths");
        var packageResult = await projects.RestoreTransferPackageAsync(package);
        Check((await projects.LoadAsync(packageResult.ProfilePath)).Settings.AutoplayProfiles.Single().RandomPoolSetupId == setupId,
            "Single-file transfer retains the Autoplay setup link");
        Check((await projects.LoadAsync(packageResult.ProfilePath)).Name == "Dalen" && packageResult.MissingMediaCount == 0, "Single-file package is independently usable after source deletion");
        var older = await projects.RestoreTransferVersionAsync(relocated, versions.Single(version => version.Manifest.ProfileFile == first.Manifest.ProfileFile));
        Check((await projects.LoadAsync(older.ProfilePath)).Decks[0].Jingles[0].Title == "Mål", "An older immutable version remains independently restorable");
        Directory.Move(relocated, relocated + "-offline");
        Check(File.Exists(loaded.Decks[0].Jingles[0].FilePath) && File.Exists(loaded.Settings.AutoplayQueueItems.Single().FilePath) &&
            (await PlaylistService.Shared.PrepareAsync(loaded.Settings.AutoplayProfiles.Single().PlaylistPath!, null, forceFresh: true))!.ExistingEntries.Length == 3,
            "Imported audio and playlists work while the entire Drive/USB source is unavailable");
        Directory.Move(relocated + "-offline", relocated);

        var selected = versions.Single(version => version.Manifest.ProfileFile == second.Manifest.ProfileFile);
        var playlistMedia = selected.Manifest.Files.Keys.First(path => path.StartsWith("Media") && path.EndsWith(Path.GetFileName(prepared.ExistingEntries[0].FilePath)));
        var missing = Path.Combine(relocated, playlistMedia);
        var bytes = await File.ReadAllBytesAsync(missing); File.Delete(missing);
        await ExpectFailure(projects.RestoreTransferVersionAsync(relocated, selected));
        await File.WriteAllBytesAsync(missing, [255, 0]);
        await ExpectFailure(projects.RestoreTransferVersionAsync(relocated, selected));
        await File.WriteAllBytesAsync(missing, bytes);
    }

    private static async Task CheckCloseAndDialogs(string root, string? output)
    {
        var projects = new ProjectService();
        var preferences = new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath);
        var path = Path.Combine(root, "close.floorballdj.json");
        await projects.SaveAsync(ProjectService.CreateDefault(), path); preferences.SetDefaultProfile(path);
        var main = new MainWindow(new LicenseService(), shutdownApplicationOnClose: false) { ShowInTaskbar = false };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        main.Show();
        while (!main.StartupReady) await Task.Delay(20);
        var vm = (MainViewModel)main.DataContext;
        vm.Decks[0].Jingles[0].Title = "Warning"; vm.RequestSave();
        main.ConfirmUnsavedProfile = () => UnsavedProfileChoice.Cancel;
        main.Close(); await Task.Delay(50);
        Check(main.IsVisible && !closed.Task.IsCompleted && vm.HasUnsavedChanges, "Cancel keeps the real window and edits open");
        main.ConfirmUnsavedProfile = () => UnsavedProfileChoice.Save;
        main.SaveProfileForTest = () => Task.FromResult(false);
        main.Close(); await Task.Delay(50);
        Check(main.IsVisible && !closed.Task.IsCompleted, "Failed/cancelled save blocks real close");
        await vm.FlushSavesAsync();
        var original = await File.ReadAllBytesAsync(path);
        main.ConfirmUnsavedProfile = () => UnsavedProfileChoice.Discard;
        main.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(!File.Exists(vm.WorkingCopyPath) && (await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Real Don't save closes without changing the saved profile");

        foreach (var language in new[] { "sv", "en" })
        {
            LanguageService.SetLanguage(language);
            var prompt = new UnsavedProfileWindow("IBK Dalen – Matchprofil") { ShowInTaskbar = false };
            prompt.Show(); LanguageService.TranslateTree(prompt); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (output is not null) Render(prompt, Path.Combine(output, $"unsaved-{language}.png"));
            prompt.Close();
            var version = new TransferVersion("fixture", new() { ProjectName = "IBK Dalen", ComputerName = "Föreningsdator", CreatedAt = DateTimeOffset.Now });
            var picker = new TransferVersionWindow([version]) { ShowInTaskbar = false };
            picker.Show(); LanguageService.TranslateTree(picker); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (output is not null) Render(picker, Path.Combine(output, $"transfer-{language}.png"));
            picker.Close();
        }
    }

    private static void Render(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
        throw new InvalidOperationException("Expected transfer/save failure was swallowed");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
