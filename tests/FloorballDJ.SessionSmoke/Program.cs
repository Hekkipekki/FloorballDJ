using System.IO;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                foreach (var source in new[] { "new", "autosave", "default", "missing-default" })
                foreach (var wasEnabled in new[] { true, false })
                    await CheckStartup(source, wasEnabled);
                Console.WriteLine("PASS: fresh startup sessions, autosave/default/fallback, disabled session, all decks/pages, unplayed priority and second restart.");
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task CheckStartup(string source, bool wasEnabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "FloorballDJ-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", root);
        var projects = new ProjectService();
        var preferences = new ProfilePreferencesService(root, projects.DefaultProjectPath);
        var project = ProjectService.CreateDefault();
        project.Decks[0].PageCount = 2;
        ProjectService.EnsureLayout(project);
        project.Settings.TrackSession = wasEnabled;
        project.Settings.TitleFontSize = 22;
        preferences.SetLocalTitleFontSize(22);
        foreach (var jingle in project.Decks.SelectMany(deck => deck.Jingles)) jingle.SessionPlayCount = 3;
        if (source != "new") await projects.SaveAsync(project, projects.DefaultProjectPath);
        if (source is "default" or "missing-default")
        {
            var path = Path.Combine(root, "match.floorballdj.json");
            await projects.SaveAsync(project, path);
            preferences.SetDefaultProfile(path);
            if (source == "missing-default") File.Move(path, Path.Combine(root, "moved.floorballdj.json"));
        }

        using (var audio = new AudioEngine())
        using (var vm = new MainViewModel(projects, preferences, audio))
        {
            await vm.InitializeAsync();
            Check(vm.Settings.TrackSession, "Session must be enabled at startup");
            Check(vm.Decks.SelectMany(d => d.Jingles).All(j => j.SessionPlayCount == 0), "All previous counts must be reset");
            if (source != "new") Check(vm.Settings.TitleFontSize == 22 && vm.Decks[0].PageCount == 2, "Profile settings must be preserved");
            var candidates = vm.Decks[0].Jingles.Take(2).Select(j => new RandomPoolCandidate(j, vm.Decks[0].Id)).ToArray();
            Check(RandomPoolSelectionService.GetEligibleCandidates(candidates, true, null, false, 4, null).Length == 2, "All songs eligible in fresh session");
            candidates[0].Jingle.SessionPlayCount = 1;
            Check(RandomPoolSelectionService.GetEligibleCandidates(candidates, true, null, false, 4, null).Single().Jingle == candidates[1].Jingle, "Prioritize unplayed within the session");
            vm.SelectedDeck = vm.Decks.Last();
            await vm.SaveAsync();
            Check(candidates[0].Jingle.SessionPlayCount == 1, "Save and deck switch must not reset session");
        }
        using (var audio = new AudioEngine())
        using (var reopened = new MainViewModel(projects, preferences, audio))
        {
            await reopened.InitializeAsync();
            Check(reopened.Settings.TrackSession && reopened.Decks.SelectMany(d => d.Jingles).All(j => j.SessionPlayCount == 0), "Second launch must reset newly saved counts too");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
