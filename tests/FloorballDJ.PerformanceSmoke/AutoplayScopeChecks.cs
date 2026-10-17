using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;

internal static class AutoplayScopeChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var project = ProjectService.CreateDefault();
        var dalen = new RandomPoolSetup { Name = "Dalen" };
        var dam = new RandomPoolSetup { Name = "Dalendam" };
        project.Settings.RandomPoolSetups = [dalen, dam];
        project.Settings.ActiveRandomPoolSetupId = dalen.Id;
        var first = new AutoplayProfile { Name = "Dalen paus", Shortcut = "F8", RandomPoolSetupId = dalen.Id };
        var second = new AutoplayProfile { Name = "Dam paus", Shortcut = "F8", RandomPoolSetupId = dam.Id };
        var global = JsonSerializer.Deserialize<AutoplayProfile>("{\"Shortcut\":\"F7\"}")!;
        project.Settings.AutoplayProfiles = [second, first, global];
        Check(AutoplayShortcutService.Find(project.Settings, "F8") == first, "Active setup selects the correct playlist independent of order");
        Check(global.RandomPoolSetupId is null && AutoplayShortcutService.Find(project.Settings, "F7") == global, "Legacy presets remain global");
        project.Settings.ActiveRandomPoolSetupId = dam.Id;
        dam.Name = "Dam renamed";
        Check(AutoplayShortcutService.Find(project.Settings, "F8") == second && AutoplayShortcutService.Find(project.Settings, "F7") == global,
            "Switching and renaming retains ID-based routing and global shortcuts");
        project.Settings.AutoplayProfiles.Remove(second);
        Check(AutoplayShortcutService.Find(project.Settings, "F8") is null, "Inactive keys cannot start a playlist");
        project.Settings.AutoplayProfiles.Insert(0, second);
        Check(!SettingsWindow.AutoplayShortcutsConflict(new() { Shortcut = "Ctrl + F8", RandomPoolSetupId = dalen.Id },
                new() { Shortcut = "Ctrl+F8", RandomPoolSetupId = dam.Id }), "Distinct setups may share a canonical key");
        Check(SettingsWindow.AutoplayShortcutsConflict(new() { Shortcut = "Ctrl + F8", RandomPoolSetupId = dalen.Id },
                new() { Shortcut = "Ctrl+F8", RandomPoolSetupId = dalen.Id }), "Overlapping canonical keys are rejected");
        Check(SettingsWindow.AutoplayShortcutsConflict(new() { Shortcut = "F8" }, new() { Shortcut = "F8", RandomPoolSetupId = dalen.Id }),
            "Global and scoped assignments overlap");
        var projects = new ProjectService();
        var prefs = new ProfilePreferencesService(Path.Combine(root, "scope-prefs"), projects.DefaultProjectPath);
        var cancel = new SettingsWindow(project, [], prefs);
        cancel.ViewData.AutoplayProfiles[0].RandomPoolSetupId = dalen.Id;
        cancel.Close();
        Check(second.RandomPoolSetupId == dam.Id, "Cancelling does not mutate the saved scope");
        var originalLanguage = LanguageService.CurrentLanguage;
        try
        {
            foreach (var language in new[] { "sv", "en" })
            {
                LanguageService.SetLanguage(language);
                var save = new SettingsWindow(project, [], prefs) { ShowInTaskbar = false };
                Check((bool)typeof(SettingsWindow).GetMethod("ConfirmAutoplayShortcutReplacement", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(save, [save.ViewData.AutoplayProfiles[0], "F8"])!, "Assigning the same key in separate scopes needs no replacement");
                Exception? failure = null;
                _ = save.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        var combo = (ComboBox)save.FindName("AutoplayRandomSetupCombo");
                        Check(combo.Items.Count == 3 && (Guid)combo.SelectedValue == dam.Id, "Dropdown restores selected scope");
                        combo.SelectedValue = Guid.Empty;
                        Check(save.ViewData.SelectedAutoplayProfile!.RandomPoolSetupId == Guid.Empty, "All profiles can be selected");
                        combo.SelectedValue = dam.Id;
                        Check(save.ViewData.SelectedAutoplayProfile!.RandomPoolSetupId == dam.Id, "Selection updates the draft");
                        save.ViewData.SelectedAutoplayProfile = save.ViewData.AutoplayProfiles[1];
                        combo.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)!.UpdateTarget();
                        Check((Guid)combo.SelectedValue == dalen.Id, "Each list restores its own scope");
                        if (output is not null)
                        {
                            LanguageService.TranslateTree(save);
                            Directory.CreateDirectory(output);
                            var parent = VisualTreeHelper.GetParent(combo);
                            while (parent is not GroupBox) parent = VisualTreeHelper.GetParent(parent);
                            var group = (GroupBox)parent;
                            group.BringIntoView(); save.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(group.ActualWidth), (int)Math.Ceiling(group.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            var drawing = new DrawingVisual();
                            using (var context = drawing.RenderOpen())
                                context.DrawRectangle(new VisualBrush(group), null, new Rect(0, 0, group.ActualWidth, group.ActualHeight));
                            bitmap.Render(drawing);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(output, $"autoplay-scope-{language}.png")); encoder.Save(stream);
                        }
                        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(save, [save, new RoutedEventArgs()]);
                    }
                    catch (Exception ex) { failure = ex; save.Close(); }
                }));
                var accepted = save.ShowDialog();
                Check(accepted == true && failure is null, "Real settings save accepts separate scopes: " + failure);
                Check(project.Settings.AutoplayProfiles[0].RandomPoolSetupId == dam.Id && project.Settings.AutoplayProfiles[1].RandomPoolSetupId == dalen.Id &&
                    project.Settings.AutoplayProfiles[2].RandomPoolSetupId is null, "Save preserves scoped and global presets through settings copies");
            }
        }
        finally { LanguageService.SetLanguage(originalLanguage); }
        var path = Path.Combine(root, "scoped-profile.floorballdj.json");
        await projects.SaveAsync(project, path);
        var loaded = await projects.LoadAsync(path);
        Check(AutoplayShortcutService.Find(loaded.Settings, "F8")?.RandomPoolSetupId == dam.Id, "Restart retains routing");
        loaded.Settings.RandomPoolSetups.RemoveAll(setup => setup.Id == dam.Id);
        loaded.Settings.ActiveRandomPoolSetupId = dalen.Id;
        Check(!AutoplayShortcutService.IsAvailable(loaded.Settings.AutoplayProfiles[0], loaded.Settings), "Deleted scopes stay inactive");
        var missing = new SettingsWindow(loaded, [], prefs);
        Check(missing.ViewData.AutoplayRandomSetups.Any(choice => choice.Id == dam.Id) && missing.ViewData.AutoplayProfiles[0].RandomPoolSetupId == dam.Id,
            "Deleted scope remains visible for reassignment");
        missing.Close();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
