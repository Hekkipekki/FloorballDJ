using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;

internal static class LocalTypographyChecks
{
    internal static async Task RunAsync(string root)
    {
        var projects = new ProjectService();
        var directory = Path.Combine(root, "local-typography");
        Directory.CreateDirectory(directory);
        var shared = Path.Combine(directory, "shared.floorballdj.json");
        await projects.SaveAsync(ProjectService.CreateDefault(), shared);
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(shared))!;
        legacy["settings"]!["titleFontSize"] = 22;
        await File.WriteAllTextAsync(shared, legacy.ToJsonString());
        Check((await projects.LoadAsync(shared)).Settings.TitleFontSize == 22, "Legacy profile size can migrate");
        var a = new ProfilePreferencesService(Path.Combine(directory, "computer-a"), projects.DefaultProjectPath);
        var b = new ProfilePreferencesService(Path.Combine(directory, "computer-b"), projects.DefaultProjectPath);
        a.SetDefaultProfile(shared); b.SetDefaultProfile(shared);
        using (var first = new MainViewModel(projects, a, new AudioEngine()))
        using (var second = new MainViewModel(projects, b, new AudioEngine()))
        {
            await first.InitializeAsync(); await second.InitializeAsync();
            Check(first.Settings.TitleFontSize == 22 && second.Settings.TitleFontSize == 22, "First startup imports the old size once");
            first.Settings.TitleFontSize = 18; second.Settings.TitleFontSize = 28;
            await first.SaveAsync(shared);
            Check(!File.ReadAllText(shared).Contains("titleFontSize", StringComparison.OrdinalIgnoreCase), "Shared profile does not export local typography");
            await second.LoadAsync(shared);
            Check(second.Settings.TitleFontSize == 28, "Loading the other computer's saved profile keeps local size");
            var oldRevision = Path.Combine(directory, "old-revision.json");
            await File.WriteAllTextAsync(oldRevision, legacy.ToJsonString());
            await first.RestoreRevisionAsync(oldRevision);
            Check(first.Settings.TitleFontSize == 18, "Legacy revision cannot overwrite an established local choice");
            var backup = await projects.CreateMediaBackupAsync(first.Project, directory);
            Check(!File.ReadAllText(backup.ProfilePath).Contains("titleFontSize", StringComparison.OrdinalIgnoreCase), "Move backup excludes local typography");
            await second.LoadAsync(backup.ProfilePath);
            Check(second.Settings.TitleFontSize == 28, "Restoring a move backup keeps the destination computer's size");
            var cancel = new SettingsWindow(first.Project, [], a);
            cancel.ViewData.Draft.TitleFontSize = 35;
            cancel.Close();
            Check(first.Settings.TitleFontSize == 18 && a.GetLocalTitleFontSize() == 18, "Cancelling a settings draft preserves local and visible size");
            var save = new SettingsWindow(first.Project, [], a);
            save.ViewData.Draft.TitleFontSize = 24;
            save.ShowInTaskbar = false;
            _ = save.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(save, [save, new RoutedEventArgs()])));
            Check(save.ShowDialog() == true && first.Settings.TitleFontSize == 24 && a.GetLocalTitleFontSize() == 24,
                "Real settings save applies and persists only this computer's size");
            Check(b.GetLocalTitleFontSize() == 28 && a.GetDefaultProfilePath() == shared && a.GetRecentProfiles().Contains(shared),
                "Other computer, default profile and recent profiles are preserved");
        }
        using (var restarted = new MainViewModel(projects, new ProfilePreferencesService(Path.Combine(directory, "computer-a"), projects.DefaultProjectPath), new AudioEngine()))
        {
            await restarted.InitializeAsync();
            Check(restarted.Settings.TitleFontSize == 24, "Restart reuses the local choice");
        }
        var invalid = new ProfilePreferencesService(Path.Combine(directory, "bounds"), projects.DefaultProjectPath);
        invalid.SetLocalTitleFontSize(100); Check(invalid.GetLocalTitleFontSize() == 40, "Local size respects UI bounds");
        try { invalid.SetLocalTitleFontSize(double.NaN); throw new InvalidOperationException("NaN accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check(JsonSerializer.Deserialize<AppSettings>("{}")!.TitleFontSize == 15, "Fresh default remains 15");
        Console.WriteLine("PASS: local typography migration, two computers, profile/revision/backup, settings save/cancel, restart and bounds.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
