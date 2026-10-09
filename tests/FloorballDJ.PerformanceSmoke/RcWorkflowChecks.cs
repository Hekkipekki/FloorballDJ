using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class RcWorkflowChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        await Backup(root);
        await LocalShortcuts(root);
        await Fades(root);
        var window = new TeamDeckWindow(new TeamDeckProfile { Name = "Close fixture" }, ProjectService.CreateDefault(), (_, _, _, _) => { });
        var closed = false; window.Closed += (_, _) => closed = true;
        window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(closed && !window.IsVisible, "Real modeless Team Deck close button closes the window");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "rc-workflow-checks.json"), JsonSerializer.Serialize(new {
                passed = true, portableProfileName = true, linkedPlaylistsAndMedia = true, relativePlaylistRelocation = true,
                independentRandomSetupShortcuts = true, smoothPcmFadeEndpoints = true, shortTailFade = true, modelessTeamClose = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: portable profile name, saved playlists/order/flags and playlist-only audio after source removal; independent random-setup shortcuts; smooth PCM fades/short-tail duration; real modeless Team Deck close.");
    }

    private static async Task Backup(string root)
    {
        var source = Path.Combine(root, "portable-workflow-source"); Directory.CreateDirectory(source);
        var files = Enumerable.Range(0, 3).Select(i => Path.Combine(source, $"audio-{i}.wav")).ToArray();
        for (var i = 0; i < files.Length; i++) await File.WriteAllBytesAsync(files[i], new byte[] { (byte)i, 42, 99 });
        var playlist = Path.Combine(source, "modern.fdjplaylist.json");
        await PlaylistService.Shared.SaveAsync(playlist, new(2, true, false, [new("Extra", files[1]), new("Deck", files[0]), new("Extra", files[1])]));
        var legacy = Path.Combine(source, "legacy.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new[] { new PlaylistEntry("Legacy", files[2]) }));
        var project = ProjectService.CreateDefault(); project.Name = "Ny";
        project.Decks[0].Jingles[0].FilePath = files[0];
        project.Settings.AutoplayProfiles = [new() { Name = "Modern", PlaylistPath = playlist, Shortcut = "F8", VolumeDb = -3 }, new() { Name = "Legacy", PlaylistPath = legacy }];
        project.Settings.AutoplayLastPlaylistPath = playlist;
        var before = JsonSerializer.Serialize(project);
        var service = new ProjectService();
        var backup = await service.CreateMediaBackupAsync(project, Path.Combine(root, "workflow-backups"), profileName: "Matchprofil");
        Check(before == JsonSerializer.Serialize(project), "Backup never mutates the open profile");
        Check(Path.GetFileName(backup.ProfilePath) == "Matchprofil.floorballdj.json" && backup.MediaFileCount == 3 && backup.MissingFiles.Count == 0,
            "Backup name follows the shown profile name and includes playlist-only audio");
        // Only fixture files created above are removed, so relocation cannot accidentally use source audio.
        foreach (var file in files) File.Delete(file); File.Delete(playlist); File.Delete(legacy);
        var restored = await service.RestorePortableBackupAsync(backup.Directory);
        var copy = await service.LoadAsync(restored.ProfilePath);
        Check(copy.Name == "Matchprofil" && copy.Settings.AutoplayProfiles[0].VolumeDb == -3 && copy.Settings.AutoplayProfiles[0].Shortcut == "F8",
            "Restored name and saved playlist profile settings survive");
        Check(copy.Settings.AutoplayLastPlaylistPath == copy.Settings.AutoplayProfiles[0].PlaylistPath, "Last saved playlist resolves to the portable copy");
        var modern = await PlaylistService.Shared.PrepareAsync(copy.Settings.AutoplayProfiles[0].PlaylistPath!, null, forceFresh: true);
        var old = await PlaylistService.Shared.PrepareAsync(copy.Settings.AutoplayProfiles[1].PlaylistPath!, null, forceFresh: true);
        Check(modern!.Definition.ShuffleEnabled && !modern.Definition.LoopEnabled && modern.ExistingEntries.Select(e => e.Title).SequenceEqual(new[] { "Extra", "Deck", "Extra" }) && old!.ExistingEntries.Single().Title == "Legacy",
            "Portable modern/legacy playlist flags, order and duplicates survive");
        foreach (var entry in modern.ExistingEntries.Concat(old!.ExistingEntries))
            Check(entry.FilePath.StartsWith(restored.Directory, StringComparison.OrdinalIgnoreCase) && File.Exists(entry.FilePath), "Restored media does not depend on original disk paths");
        Check(File.ReadAllBytes(modern.ExistingEntries[0].FilePath).SequenceEqual(new byte[] { 1, 42, 99 }), "Playlist-only audio bytes are unchanged");
    }

    private static async Task LocalShortcuts(string root)
    {
        var project = RandomSettingsChecks.Fixture(root, 12, 1);
        var first = project.Settings.RandomPoolSetups[0];
        var second = new RandomPoolSetup { Name = "Other", Profiles = [new() { Name = "Other group", Shortcut = "F9" }] };
        project.Settings.RandomPoolSetups.Add(second);
        var external = project.Decks[0].Jingles[0]; external.Shortcut = "F10";
        var window = new RandomPlayerSettingsWindow(project);
        try
        {
            Check(await window.Initialization, "Random setup fixture initialized");
            var profile = window.ViewData.SelectedProfile!;
            Check((bool)typeof(RandomPlayerSettingsWindow).GetMethod("ConfirmShortcutReplacement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [profile, "F10"])!,
                "Assignment is scoped to the selected random setup without stealing an external jingle key");
            profile.Shortcut = "F10";
            window.ViewData.SelectedSetup = window.ViewData.Setups[1]; window.ViewData.SelectedProfile!.Shortcut = "F10";
            Check(window.TryApplyDraft(), "The same key is accepted in separate random setups");
        }
        finally { window.Close(); }
        var service = new ProjectService(); var path = Path.Combine(root, "random-local.json"); await service.SaveAsync(project, path);
        var saved = await service.LoadAsync(path);
        Check(saved.Settings.RandomPoolSetups.All(setup => setup.Profiles[0].Shortcut == "F10") && external.Shortcut == "F10", "Local keys persist and external key remains");
        var again = new RandomPlayerSettingsWindow(saved);
        try
        {
            Check(await again.Initialization, "Reopened random setup fixture initialized");
            again.ViewData.SelectedSetup = again.ViewData.Setups[0]; again.ViewData.SelectedProfile!.Shortcut = "F11";
            Check(again.TryApplyDraft() && saved.Settings.RandomPoolSetups[0].Profiles[0].Shortcut == "F11" && saved.Settings.RandomPoolSetups[1].Profiles[0].Shortcut == "F10",
                "Editing one setup cannot change the other setup's key");
        }
        finally { again.Close(); }
    }

    private sealed class UnitSource(int rate, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, 1f, offset, count); return count; }
    }
    private static async Task Fades(string root)
    {
        foreach (var rate in new[] { 44100, 48000 }) foreach (var channels in new[] { 1, 2 })
        {
            var provider = new SmoothGainSampleProvider(new UnitSource(rate, channels)); provider.SetTarget(0, .2);
            var samples = new List<float>(); var block = new float[127 * channels];
            while (!provider.TransitionCompleted.IsCompleted) { provider.Read(block, 0, block.Length); samples.AddRange(block); }
            provider.Read(block, 0, block.Length);
            Check(block.All(s => s == 0) && samples.Zip(samples.Skip(channels)).All(pair => pair.Second <= pair.First) && samples.TakeLast(20 * channels).All(s => s < .001),
                "Fade is monotonic, reaches silence gently and stays silent at both sample rates/channel counts");
        }
        var file = Path.Combine(root, "short-fade-silence.wav");
        using (var writer = new WaveFileWriter(file, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 3], 0, 48000 * 4 * 3);
        using var audio = new AudioEngine(); audio.Configure(null, null, -60, -12, 0, .2);
        audio.Play(new Jingle { FilePath = file, EndSeconds = .65, DurationSeconds = 3 });
        await Task.Delay(80);
        var voices = (System.Collections.IEnumerable)typeof(AudioEngine).GetField("_voices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;
        var voice = voices.Cast<object>().Single();
        var completion = audio.FadeOutAllAsync(2);
        var ramp = (SmoothGainSampleProvider)voice.GetType().GetProperty("FadeVolume")!.GetValue(voice)!;
        var frames = (long)typeof(SmoothGainSampleProvider).GetField("_totalFrames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ramp)!;
        Check(frames > 0 && frames < 48000 * .65, "Actual engine limits a two-second fade to the short unread clip tail");
        await completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(audio.GetCurrentPosition() is null, "Short-tail fade finishes and releases the voice");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
