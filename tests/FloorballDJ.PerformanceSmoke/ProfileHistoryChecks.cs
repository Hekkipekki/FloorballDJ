using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;

internal static class ProfileHistoryChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var previous = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var locale = LanguageService.CurrentLanguage;
        var directory = Path.Combine(root, "permanent-history"); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", directory);
        try
        {
            await CheckPreservation(directory);
            await CheckIsolationAndFork(directory);
            await CheckRotation(directory);
            await CheckLegacyAndFailure(directory);
            await CheckExternalOverwrite(directory);
            await CheckHistoryWindow(directory, output);
        }
        finally { Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previous); LanguageService.SetLanguage(locale); }
        Console.WriteLine("PASS: permanent per-profile backups, Don't save final edit, manual overwrite/restore safety, absolute media/playlist paths, restart, isolation/Save As, protected milestones versus 100 autosaves, legacy import, write failure and searchable Swedish/English revision log.");
    }

    private static MainViewModel Vm(ProjectService projects) => new(projects,
        new ProfilePreferencesService(projects.AppDataDirectory, projects.DefaultProjectPath), new AudioEngine());
    private static async Task<string> CreateProfile(ProjectService projects, string directory, string name = "Dalen")
    {
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, "music.wav"); await File.WriteAllBytesAsync(media, [1, 2, 3]);
        var playlist = Path.Combine(directory, "pause.fdjplaylist.json"); await File.WriteAllTextAsync(playlist, "[]");
        var project = ProjectService.CreateDefault(); project.Name = name;
        var song = project.Decks[0].Jingles[0]; song.Title = "Original låt"; song.FilePath = media;
        project.Settings.AutoplayQueueItems = [song];
        project.Settings.AutoplayClipOptions = [new(song.Id, true, .5)];
        project.Settings.AutoplayTransitionType = AutoplayTransitionType.FadeInOut;
        project.Settings.AutoplaySecondQueueItems = [new() { Title = "Andra listan", FilePath = media, GainDb = -8 }];
        project.Settings.AutoplaySecondPlaylistPath = playlist;
        project.Settings.AutoplaySecondQueueLoopEnabled = false;
        project.Settings.MusicFolderPath = directory; project.Settings.AutoplayDefaultPlaylistPath = playlist;
        project.Settings.AutoplayProfiles = [new() { Name = "Paus", PlaylistPath = playlist }];
        var path = Path.Combine(directory, name + ".floorballdj.json");
        await projects.SaveAsync(project, path); return path;
    }

    private static async Task CheckPreservation(string root)
    {
        var projects = new ProjectService(); var path = await CreateProfile(projects, Path.Combine(root, "saved"));
        var original = await File.ReadAllBytesAsync(path); string backup; string discardedRevision;
        using (var vm = Vm(projects))
        {
            await vm.LoadAsync(path); backup = vm.LocalProfileBackupPath;
            Check(Path.GetFileName(backup) == "Dalen backup.floorballdj.json" && File.Exists(backup) &&
                !Path.GetDirectoryName(backup)!.Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase), "Named backup is a separate local file");
            var song = vm.Decks[0].Jingles[0]; song.Title = "Osparad ändring"; song.GainDb = -6; vm.Decks[0].Name = "Nytt decknamn";
            vm.RequestSave();
            await vm.DiscardChangesAsync(); // Deliberately beat the 175 ms debounce.
            Check(Enumerable.SequenceEqual(original, await File.ReadAllBytesAsync(path)) && !File.Exists(vm.WorkingCopyPath), "Don't save preserves original bytes and dismisses recovery");
            var kept = await projects.LoadAsync(backup);
            Check(kept.Decks[0].Jingles[0].Title == "Osparad ändring" && kept.Decks[0].Jingles[0].GainDb == -6, "Final unsaved edits remain in the named backup");
            Check(kept.Decks[0].Jingles[0].FilePath == Path.Combine(Path.GetDirectoryName(path)!, "music.wav") &&
                kept.Settings.AutoplayProfiles[0].PlaylistPath == Path.Combine(Path.GetDirectoryName(path)!, "pause.fdjplaylist.json"), "Backup opens independently with correct media and playlist paths");
            var revisions = await vm.GetRevisionsAsync();
            var draft = revisions.Single(revision => revision.Kind == ProfileRevisionKind.Discarded);
            discardedRevision = draft.Path;
            Check(draft.ChangeLog.Contains("Osparad ändring") && draft.ChangeLog.Contains("volym") && draft.ChangeLog.Contains("decknamn"), "One revision describes multiple changes in readable language");
            Check(revisions.Any(revision => revision.IsSavedVersion), "The saved original is also available");
        }
        using (var vm = Vm(projects))
        {
            await vm.LoadAsync(path);
            Check(vm.Decks[0].Jingles[0].Title == "Original låt" && File.Exists(discardedRevision), "Restart opens the saved original and retains unsaved history");
            Check((await projects.LoadAsync(backup)).Decks[0].Jingles[0].Title == "Osparad ändring", "Opening the original does not overwrite the latest unsaved backup");
            vm.Decks[0].Jingles[0].Title = "Manuellt sparad B"; await vm.SaveAsync();
            var bytesB = await File.ReadAllBytesAsync(path);
            vm.Decks[0].Jingles[0].Title = "Felaktigt sparad C"; await vm.SaveAsync();
            var bytesC = await File.ReadAllBytesAsync(path);
            var revisions = await vm.GetRevisionsAsync();
            var savedB = await Find(projects, revisions.Where(revision => revision.IsSavedVersion), "Manuellt sparad B");
            Check(!File.Exists(vm.WorkingCopyPath) && File.Exists(savedB.Path) && File.Exists(discardedRevision), "Manual saves retain both saved versions and discarded edits");
            await vm.RestoreRevisionAsync(savedB.Path);
            Check(vm.HasUnsavedChanges && vm.Decks[0].Jingles[0].Title == "Manuellt sparad B" && Enumerable.SequenceEqual(bytesC, await File.ReadAllBytesAsync(path)), "Restoring an overwritten version leaves the original untouched until Save");
            Check((await vm.GetRevisionsAsync()).Any(revision => revision.IsSavedVersion && revision.Path != savedB.Path), "Pre-restore current state stays in saved history");
            await vm.SaveAsync();
            Check(Enumerable.SequenceEqual(bytesB, await File.ReadAllBytesAsync(path)), "Manual Save after review restores the intended profile exactly");
            var broken = Path.Combine(root, "broken-revision.floorballdj.json"); await File.WriteAllTextAsync(broken, "{broken");
            var current = vm.Project;
            await Failure(vm.RestoreRevisionAsync(broken));
            Check(ReferenceEquals(current, vm.Project) && Enumerable.SequenceEqual(bytesB, await File.ReadAllBytesAsync(path)), "An unreadable revision cannot replace current work or the original");
        }
    }

    private static async Task CheckIsolationAndFork(string root)
    {
        var projects = new ProjectService();
        var first = await CreateProfile(projects, Path.Combine(root, "club-a"));
        var second = await CreateProfile(projects, Path.Combine(root, "club-b"));
        using var vm = Vm(projects); await vm.LoadAsync(first); var firstBackup = vm.LocalProfileBackupPath;
        vm.Decks[0].Jingles[0].Title = "Bara förening A"; vm.RequestSave(); await vm.FlushSavesAsync();
        var oldHistory = await vm.GetRevisionsAsync();
        var fork = Path.Combine(root, "Dalen kopia.floorballdj.json"); await vm.SaveAsync(fork);
        Check(vm.LocalProfileBackupPath != firstBackup && (await vm.GetRevisionsAsync()).Count >= oldHistory.Count,
            "Save As forks the existing history under the new profile identity");
        vm.Decks[0].Jingles[0].Title = "Bara kopian"; vm.RequestSave(); await vm.FlushSavesAsync();
        await vm.LoadAsync(second);
        Check(vm.LocalProfileBackupPath != firstBackup && (await vm.GetRevisionsAsync()).All(revision => !revision.ChangeLog.Contains("Bara förening A")), "Same-named profiles in different folders have separate history");
        await vm.LoadAsync(first);
        Check((await vm.GetRevisionsAsync()).All(revision => !revision.ChangeLog.Contains("Bara kopian")), "Later edits to Save As do not leak back to the source history");
    }

    private static async Task CheckRotation(string root)
    {
        var projects = new ProjectService(); var path = await CreateProfile(projects, Path.Combine(root, "rotation"));
        using var vm = Vm(projects); await vm.LoadAsync(path);
        vm.Decks[0].Jingles[0].Title = "Skyddad manuell version"; await vm.SaveAsync();
        var milestone = await Find(projects, (await vm.GetRevisionsAsync()).Where(revision => revision.IsSavedVersion), "Skyddad manuell version");
        for (var i = 0; i < ProjectService.AutomaticRevisionLimit + 3; i++)
        {
            vm.Decks[0].Jingles[0].Title = "Automatisk " + i; vm.RequestSave(); await vm.FlushSavesAsync();
        }
        var revisions = await vm.GetRevisionsAsync();
        Check(revisions.Count(revision => revision.Kind is ProfileRevisionKind.Autosave or ProfileRevisionKind.Legacy) == ProjectService.AutomaticRevisionLimit &&
            revisions.Any(revision => revision.Path == milestone.Path) && File.Exists(milestone.Path), "Only dense autosaves rotate; saved milestones survive");
        vm.Decks[0].Jingles[0].Title = "Sista ändringen före stängning"; vm.RequestSave(); await vm.DiscardChangesAsync();
        Check((await vm.GetRevisionsAsync()).Any(revision => revision.Kind == ProfileRevisionKind.Discarded), "Don't save creates a retained final checkpoint after rotation");
    }

    private static async Task CheckLegacyAndFailure(string root)
    {
        var projects = new ProjectService(); var path = await CreateProfile(projects, Path.Combine(root, "legacy"));
        var original = await projects.LoadAsync(path); original.Decks[0].Jingles[0].Title = "Sparad gammal B"; await projects.SaveAsync(original, path);
        var working = new WorkingCopyService(projects.AppDataDirectory); var workingPath = working.GetPath(path, original.Id);
        working.Prepare(workingPath, path, "Dalen");
        original.Decks[0].Jingles[0].Title = "Äldre osparad C"; await projects.SaveAsync(original, workingPath);
        original.Decks[0].Jingles[0].Title = "Senaste osparade D"; await projects.SaveAsync(original, workingPath);
        using var vm = Vm(projects); await vm.LoadAsync(path);
        var imported = await vm.GetRevisionsAsync();
        Check(imported.Any(revision => revision.Kind == ProfileRevisionKind.LegacySaved) && imported.Any(revision => revision.Kind == ProfileRevisionKind.Legacy), "Existing saved and working-copy revision histories are imported");
        foreach (var revision in imported)
            Check((await projects.LoadAsync(revision.Path)).Decks[0].Jingles[0].FilePath == Path.Combine(Path.GetDirectoryName(path)!, "music.wav"), "Imported relative paths preserve the original media location");
        await vm.DeleteRecoveryAsync(vm.RecoverableCopies.Single(copy => copy.SourcePath == path));
        var after = await vm.GetRevisionsAsync();
        Check(!File.Exists(workingPath) && after.Count == imported.Count && after.Any(revision => revision.Kind == ProfileRevisionKind.Discarded), "Dismissing legacy recovery preserves its latest state and revisions");
        Check((await vm.GetRevisionsAsync()).Count == after.Count, "Legacy imports are idempotent");
        var before = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(vm.LocalProfileBackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.Decks[0].Jingles[0].Title = "Backupfel får inte skriva över originalet";
            await Failure(vm.SaveAsync());
        }
        Check(Enumerable.SequenceEqual(before, await File.ReadAllBytesAsync(path)) && vm.HasUnsavedChanges, "A backup failure blocks overwriting the original and retains dirty state");
        await vm.SaveAsync();
        Check((await projects.LoadAsync(path)).Decks[0].Jingles[0].Title == "Backupfel får inte skriva över originalet", "Saving can be retried after the backup failure clears");
    }

    private static async Task CheckExternalOverwrite(string root)
    {
        var projects = new ProjectService(); var path = await CreateProfile(projects, Path.Combine(root, "external-edit"));
        using var vm = Vm(projects); await vm.LoadAsync(path);
        vm.Decks[0].Jingles[0].Title = "VersionAAAA"; await vm.SaveAsync();
        var length = new FileInfo(path).Length; var timestamp = File.GetLastWriteTimeUtc(path);
        var external = await projects.LoadAsync(path); external.Decks[0].Jingles[0].Title = "VersionBBBB";
        await projects.SaveAsync(external, path); File.SetLastWriteTimeUtc(path, timestamp);
        Check(new FileInfo(path).Length == length, "External edit fixture preserves file size and timestamp");
        vm.Decks[0].Jingles[0].Title = "VersionCCCC"; await vm.SaveAsync();
        var retained = await Find(projects, (await vm.GetRevisionsAsync()).Where(revision => revision.IsSavedVersion), "VersionBBBB");
        Check(File.Exists(retained.Path), "A manual overwrite preserves externally changed original bytes even with unchanged metadata");
    }

    private static async Task CheckHistoryWindow(string root, string? output)
    {
        var projects = new ProjectService(); var path = await CreateProfile(projects, Path.Combine(root, "history-ui"));
        using var vm = Vm(projects); await vm.LoadAsync(path);
        vm.Decks[0].Jingles[0].Title = "Felaktigt namn"; await vm.SaveAsync();
        vm.Decks[0].Jingles[0].GainDb = -9; vm.RequestSave(); await vm.FlushSavesAsync();
        foreach (var language in new[] { "sv", "en" })
        {
            LanguageService.SetLanguage(language);
            var window = new RevisionHistoryWindow(vm) { ShowInTaskbar = false };
            Exception? failure = null;
            async Task Inspect()
            {
                await Task.Delay(20);
                try
                {
                    var list = (ListView)window.FindName("RevisionList");
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (list.Items.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
                    Check(list.Items.Count > 0, "History window loads retained snapshots");
                    LanguageService.TranslateTree(window);
                    var filter = (ComboBox)window.FindName("HistoryFilter"); var search = (TextBox)window.FindName("HistorySearch");
                    filter.SelectedIndex = 1;
                    Check(list.Items.Cast<ProjectRevision>().All(revision => revision.IsSavedVersion), "Saved-version filter excludes unsaved checkpoints");
                    filter.SelectedIndex = 0; search.Text = "volym";
                    Check(list.Items.Count > 0 && list.Items.Cast<ProjectRevision>().All(revision => revision.ChangeLog.Contains("volym")), "Search includes the full change log");
                    search.Clear(); list.SelectedIndex = 0;
                    foreach (var (width, height) in new[] { (1240d, 780d), (1040d, 680d) })
                    {
                        window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var restore = (Button)window.FindName("RestoreButton");
                        Check(restore.IsEnabled && ((TextBox)window.FindName("ChangeLogText")).Text.Length > 0, "Selected version exposes its change log and review action");
                        var content = (FrameworkElement)window.Content;
                        var bounds = restore.TransformToAncestor(content).TransformBounds(new Rect(restore.RenderSize));
                        Check(bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, "Restore action remains visible at laptop and desktop sizes");
                        if (output is not null) Save(content, output, $"profile-history-{language}-{width}.png");
                    }
                    var originalRevision = await Find(projects, list.Items.Cast<ProjectRevision>().Where(revision => revision.IsSavedVersion), "Original låt");
                    list.SelectedItem = originalRevision;
                    ((Button)window.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { failure = exception; window.Close(); }
            }
            var inspection = Inspect();
            var accepted = window.ShowDialog(); await inspection;
            if (failure is not null) throw failure;
            Check(accepted == true && vm.HasUnsavedChanges && vm.Decks[0].Jingles[0].Title == "Original låt" &&
                (await projects.LoadAsync(path)).Decks[0].Jingles[0].Title == "Felaktigt namn", "UI restore opens the selected old version for review without overwriting the original");
            // Prepare another unsaved state so both locales exercise log search.
            vm.Decks[0].Jingles[0].GainDb = -12; vm.RequestSave(); await vm.FlushSavesAsync();
        }
    }

    private static async Task<ProjectRevision> Find(ProjectService projects, IEnumerable<ProjectRevision> revisions, string title)
    {
        foreach (var revision in revisions) if ((await projects.LoadAsync(revision.Path)).Decks[0].Jingles[0].Title == title) return revision;
        throw new InvalidOperationException("Missing retained version: " + title);
    }
    private static async Task Failure(Task work)
    {
        try { await work; } catch { return; }
        throw new InvalidOperationException("Expected operation to fail safely");
    }
    private static void Save(FrameworkElement element, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var visual = new DrawingVisual(); var offset = VisualTreeHelper.GetOffset(element);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)element.FindResource("AppBackgroundBrush"), null, new Rect(element.RenderSize));
            drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(new Point(offset.X, offset.Y), element.RenderSize) }, null, new Rect(element.RenderSize));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
