using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Controls;
using FloorballDJ.Views;

internal static class RandomSettingsSearchChecks
{
    internal static async Task RunAsync(string root, string? output)
    {
        var project = RandomSettingsChecks.Fixture(root, 85, 6);
        project.Decks[0].Jingles[0].Title = "Åmål mål";
        project.Decks[1].Jingles[0].Title = "Mål Åmål";
        var before = JsonSerializer.Serialize(project.Settings);
        var window = new RandomPlayerSettingsWindow(project) { ShowInTaskbar = false };
        try
        {
            window.Show(); Check(await window.Initialization, "Search fixture is shown and prepared"); await Idle();
            var search = (TextBox)window.FindName("SearchBox"); var tabs = (TabControl)window.FindName("DeckTabs");
            var group = window.ViewData.SelectedProfile!;
            group.Decks[0].IncludeWholeDeck = false; // Individual cards must be enabled for real focus checks.
            var views = group.Decks.Select(deck => deck.VisibleJingles).ToArray();
            var resets = 0;
            NotifyCollectionChangedEventHandler handler = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
            foreach (var view in views) ((INotifyCollectionChanged)view).CollectionChanged += handler;
            try
            {
                search.Text = "amal mal"; await Idle();
                Check(views[0].Count == 1 && views[1].Count == 1 && views[2].Count == 0 &&
                    ReferenceEquals(tabs.SelectedItem, group.Decks[0]), "All-token accent-insensitive matching selects the exact best deck");
                window.Activate(); var songs = Songs(window); Check(songs.FocusItem(0), "Filtered song accepts focus");
                var card = Keyboard.FocusedElement; var work = songs.Panel!.Work; var previous = resets;
                search.Text = "  ÅMÅL   MÅL  "; await Idle();
                Check(resets == previous && songs.Panel.Work == work && ReferenceEquals(Keyboard.FocusedElement, card),
                    "Equivalent query retains focused checkbox and emits no view Reset or new containers");

                // The same visible identities can have a different best-deck score.
                // Skipping the entire query would keep the wrong selected tab.
                search.Text = "mal amal"; await Idle();
                Check(resets == previous && ReferenceEquals(tabs.SelectedItem, group.Decks[1]) && Songs(window).Items.Count == 1,
                    "Changed ranking selects a new best deck without resetting unchanged filtered views");
                search.Text = "nothing matches"; await Idle();
                Check(views.All(view => view.Count == 0) && Songs(window).Panel!.RealizedCount == 0, "Changed empty result removes all old cards");
                previous = resets; search.Text = "NOTHING MATCHES"; await Idle();
                Check(resets == previous, "Equivalent empty result does not reset the view");
                search.Clear(); await Idle();
                Check(views.Select(view => view.Count).SequenceEqual(new[] { 40, 40, 5 }), "Clearing a real filter restores every source item");
                previous = resets;
                var deck = group.Decks[1];
                var order = deck.Jingles.Reverse().ToArray(); deck.ApplyOrder(order); await Idle();
                Check(resets > previous && deck.VisibleJingles.Cast<RandomPlayerJingleEditor>().SequenceEqual(order), "Source permutation updates visible order independently of search");
                previous = resets; Call(window, "ApplySearch", ""); await Idle();
                Check(resets == previous && deck.VisibleJingles.Cast<RandomPlayerJingleEditor>().SequenceEqual(order), "Repeated query preserves the changed source order without another Reset");

                var added = new RandomPlayerJingleEditor { JingleId = Guid.NewGuid(), Title = "Added match", SearchText = "Added match", OriginalOrder = 40 };
                deck.Jingles.Add(added); await Idle();
                previous = resets; Call(window, "ApplySearch", ""); await Idle();
                Check(resets == previous && deck.VisibleJingles.Contains(added) && deck.MatchSummary.Contains("41"), "New source item is visible and counted with the same empty query");
                search.Text = "added match"; await Idle();
                Check(deck.VisibleJingles.Count == 1 && ReferenceEquals(deck.VisibleJingles.GetItemAt(0), added), "Same model retains newly added matching data");
                deck.Jingles.Remove(added); await Idle();
                Call(window, "ApplySearch", search.Text); await Idle();
                Check(deck.VisibleJingles.Count == 0, "Removed source item is absent despite an unchanged query");
            }
            finally { foreach (var view in views) ((INotifyCollectionChanged)view).CollectionChanged -= handler; }

            var untouched = window.ViewData.Profiles[1]; Check(!untouched.HasRealizedDecks, "Next group begins lazy");
            window.ViewData.SelectedProfile = untouched; await Idle();
            Check(untouched.HasRealizedDecks && search.Text.Length == 0 && untouched.Decks.All(deck => deck.VisibleJingles.Count == deck.Jingles.Count),
                "Switching to a lazy group clears search and shows its complete fresh draft");
            window.ViewData.SelectedProfile = group; await Idle();
            Check(group.Decks.All(deck => deck.VisibleJingles.Count == deck.Jingles.Count), "Returning to a filtered group restores its own result");
            window.ViewData.SelectedSetup = window.ViewData.Setups[1]; await Idle();
            Check(window.ViewData.SelectedProfile!.Decks.All(deck => deck.VisibleJingles.Count == deck.Jingles.Count), "Another setup is not skipped because the query is already empty");
            Check(JsonSerializer.Serialize(project.Settings) == before, "Search, ranking, source edits and group changes remain isolated from original settings");
        }
        finally { window.Close(); await Idle(); }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "random-settings-search-checks.json"), JsonSerializer.Serialize(new {
                version = "p4.5-search-checks-v1", passed = true, checkedUtc = DateTimeOffset.UtcNow,
                checks = "Equivalent/empty queries preserve focused controls and emit no Reset; ranking changes with identical hits, real filter/empty restoration, source reorder/add/remove, lazy group/setup and draft isolation",
                limitation = "Synthetic shown desktop WPF fixture; source mutations exercise collection contracts, not an external media import. Real profile, physical hardware and long-session qualification remain separate."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: unchanged search retains controls/focus; ranking, changed filters, source mutations and lazy group/setup results remain current.");
    }
    private static VirtualizingSongItemsControl Songs(Window window) => Children(window).OfType<VirtualizingSongItemsControl>().Single();
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Call(Window window, string name, params object?[] args) => window.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
