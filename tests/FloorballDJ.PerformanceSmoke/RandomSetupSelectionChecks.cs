using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Views;

internal static class RandomSetupSelectionChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var samples = new List<object>();
        foreach (var files in new[] { 553, 2000 })
        {
            var project = RandomSettingsChecks.Fixture(root, files, 12);
            var window = new RandomPlayerSettingsWindow(project) { ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                Check(await window.Initialization, "Selection fixture ready");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var combo = (ComboBox)window.FindName("SetupsCombo");
                var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
                var hits = new List<string>();
                foreach (var x in new[] { .1, .5, .9 }) foreach (var y in new[] { .1, .5, .9 })
                {
                    var hit = combo.InputHitTest(new Point(combo.ActualWidth * x, combo.ActualHeight * y)) as DependencyObject;
                    var opens = false;
                    for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
                        if (ReferenceEquals(node, toggle)) { opens = true; break; }
                    if (opens) hits.Add($"{x},{y}");
                }
                Console.WriteLine($"Dropdown hit area: {hits.Count}/9 points; combo {combo.ActualWidth}x{combo.ActualHeight}, toggle {toggle.ActualWidth}x{toggle.ActualHeight}");
                for (var trial = 0; trial < 6; trial++)
                {
                    var target = window.ViewData.Setups[(trial + 1) % window.ViewData.Setups.Count];
                    combo.IsDropDownOpen = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    Check(popup.IsOpen, "Dropdown opens");
                    var probes = window.Work.FileProbes;
                    var started = Stopwatch.GetTimestamp();
                    combo.SelectedItem = target;
                    var setter = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    combo.IsDropDownOpen = false;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var settled = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Check(ReferenceEquals(window.ViewData.SelectedSetup, target) && target.Profiles.Contains(window.ViewData.SelectedProfile!), "One selection updates setup and group");
                    var tabs = (TabControl)window.FindName("DeckTabs");
                    Check(tabs.SelectedContent is RandomPlayerDeckEditor deck && window.ViewData.SelectedProfile!.Decks.Contains(deck), "Selected songs belong to the new setup");
                    Check(window.Work.FileProbes == probes, "Selection does not rescan audio files");
                    samples.Add(new { files, trial, setterMs = setter, settledMs = settled, hitPoints = hits.Count });
                    Console.WriteLine($"{files} files, selection {trial}: setter {setter:0.0} ms; settled {settled:0.0} ms");
                }
                Check(hits.Count == 9, "The entire dropdown surface must respond to clicks, including top and bottom");
                combo.IsEditable = true;
                window.UpdateLayout();
                var editor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                var textHit = combo.InputHitTest(new Point(20, combo.ActualHeight / 2)) as DependencyObject;
                Check(editor.IsVisible && IsWithin(textHit, editor), "Editable dropdowns still route text-area clicks to the text box");
            }
            finally { window.Close(); }
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "random-setup-selection.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: full dropdown click surface, popup opening, setup/group/song selection without file scans and editable text hit testing.");
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
