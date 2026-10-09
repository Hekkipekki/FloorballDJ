using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using FloorballDJ.Models;
using FloorballDJ.Services;

internal static class ShortcutChecks
{
    internal static void Run()
    {
        CheckKeyEncoding();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var decks = CreateDecks(80);
            using var index = new JingleShortcutIndex(decks);
            CheckAgainstLegacy(decks, index);
            var cart = decks[0].Jingles[0];
            cart.Shortcut = "Control + F6"; cart.CategoryShortcut = "F8"; cart.Category = "  I  ";
            CheckAgainstLegacy(decks, index);
            cart.FilePath = "";
            CheckAgainstLegacy(decks, index);
            cart.FilePath = "new-source.wav"; cart.Id = Guid.NewGuid();
            decks[0].Id = Guid.NewGuid();
            CheckAgainstLegacy(decks, index);
            decks[0].Jingles.Move(0, 7);
            decks.Move(0, 2);
            CheckAgainstLegacy(decks, index);
            decks[0].Jingles[0] = new Jingle { FilePath = "replacement.wav", Shortcut = "F6", CategoryShortcut = "F8", Category = "i" };
            CheckAgainstLegacy(decks, index);
            var oldCollection = decks[0].Jingles;
            decks[0].Jingles = [new() { FilePath = "new-collection.wav", Shortcut = "F6", CategoryShortcut = "F8", Category = "I" }];
            CheckAgainstLegacy(decks, index);
            oldCollection.Add(new Jingle { FilePath = "removed-source.wav", Shortcut = "F6" });
            CheckAgainstLegacy(decks, index);
            var shared = decks[0].Jingles[0];
            decks[1].Jingles.Add(shared); decks.Add(decks[1]);
            CheckAgainstLegacy(decks, index);
            // Distinct objects with identical IDs and different availability must preserve
            // the first *existing* source selected after the original validation step.
            var duplicate = new Jingle { Id = shared.Id, FilePath = "duplicate.wav", Shortcut = "F6", CategoryShortcut = "F8" };
            decks[0].Jingles.Insert(0, duplicate);
            shared.FilePath = "missing.wav";
            CheckAgainstLegacy(decks, index);
            foreach (var culture in new[] { "sv-SE", "tr-TR", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CheckAgainstLegacy(decks, index);
            }
            decks[1].Jingles.Clear();
            CheckAgainstLegacy(decks, index);
            decks.RemoveAt(0);
            CheckAgainstLegacy(decks, index);
            decks.Clear();
            CheckAgainstLegacy(decks, index);
            var replacementProfile = CreateDecks(40);
            index.SetDecks(replacementProfile);
            CheckAgainstLegacy(replacementProfile, index);
            decks.Add(new Deck { Jingles = [new() { FilePath = "old-profile.wav", Shortcut = "F6" }] });
            CheckAgainstLegacy(replacementProfile, index);
            CheckSelectiveValidation();
            var heldDecks = CreateDecks(10);
            var weak = CreateDisposedIndex(heldDecks);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!weak.IsAlive, "Disposed index must not be retained by a live profile's events");
            GC.KeepAlive(heldDecks);
            Console.WriteLine("PASS: shortcut encoding, duplicate/order/category/culture parity, edits and ID changes, collection/profile replacement, shared references, selective live-file checks and subscription disposal.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    internal static void Benchmark(string output)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        foreach (var count in new[] { 553, 1000, 5000 })
        {
            var decks = CreateDecks(count);
            foreach (var deck in decks)
                foreach (var jingle in deck.Jingles) { jingle.Shortcut = "F9"; jingle.CategoryShortcut = null; }
            var target = decks[^1].Jingles[^1];
            target.Shortcut = "F6";
            var selected = decks[0];
            using var index = new JingleShortcutIndex(decks);
            var watch = Stopwatch.StartNew();
            index.Refresh();
            watch.Stop();
            var rebuildMs = watch.Elapsed.TotalMilliseconds;
            var pressed = ShortcutService.Canonicalize(ShortcutService.FromKey(Key.F6, ModifierKeys.None));
            Jingle? Legacy() => LegacyAnchor(decks, Key.F6, ModifierKeys.None) ?? LegacyJingle(decks, selected, Key.F6, ModifierKeys.None);
            Jingle? Indexed() => index.FindCategoryAnchor(pressed) ?? index.FindJingle(pressed, selected);
            Check(ReferenceEquals(Legacy(), target) && ReferenceEquals(Indexed(), target), "Benchmark routes must select the same last-deck fixture");
            for (var warm = 0; warm < 32; warm++) { Legacy(); Indexed(); }
            var legacy = Measure(Legacy, target);
            var indexed = Measure(Indexed, target);
            // Include key-token creation once for a more conservative indexed measurement.
            var withInput = Measure(() =>
            {
                var token = ShortcutService.Canonicalize(ShortcutService.FromKey(Key.F6, ModifierKeys.None));
                return index.FindCategoryAnchor(token) ?? index.FindJingle(token, selected);
            }, target);
            var selectedIds = new[] { target.Id };
            var oldChecks = 0; var newChecks = 0;
            var oldPool = LegacyPool(decks, [], selectedIds, _ => { oldChecks++; return true; });
            var newPool = JingleShortcutIndex.ExistingAudio(index.GetPoolMembers([], selectedIds), _ => { newChecks++; return true; });
            Check(oldPool.SequenceEqual(newPool), "Selective validation benchmark membership parity");
            rows.Add(new { populatedJingles = count, repeats = 1000, rebuildMs, legacy, indexed, indexedIncludingInputToken = withInput,
                oneMemberPoolFileChecks = new { before = oldChecks, after = newChecks } });
        }
        File.WriteAllText(Path.Combine(output, "shortcut-benchmark.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, optimization = "p1-hotkey-index-v1", measuredUtc = DateTimeOffset.UtcNow,
            measurement = "In-process routing only; no playback, disk latency, UI input wait or speaker timing. Legacy RC1 scan compared against indexed lookup in the same process.",
            limitations = "Synthetic nonmatching category and last-deck F6 fixtures; one run per size. Wall-clock microbenchmarks are exploratory, not machine-independent timing promises. File-check counts use a deterministic predicate, not real disk timings.",
            scenarios = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(Path.GetFullPath(Path.Combine(output, "shortcut-benchmark.json")));
    }

    private static object Measure(Func<Jingle?> action, Jingle expected)
    {
        var times = new double[1000];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < times.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = action();
            times[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check(ReferenceEquals(result, expected), "Lookup result during every measured iteration");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(times);
        return new { p50Ms = times[499], p95Ms = times[949], p99Ms = times[989], maximumMs = times[^1],
            allocatedBytesPerTrigger = allocated / (double)times.Length };
    }

    private static void CheckAgainstLegacy(ObservableCollection<Deck> decks, JingleShortcutIndex index)
    {
        var selections = decks.Cast<Deck?>().Append(null).Append(new Deck { Jingles = [new() { FilePath = "external.wav", Shortcut = "F6" }] });
        foreach (var selected in selections)
            foreach (var key in new[] { Key.F6, Key.F8, Key.F9, Key.OemPlus, Key.NumPad1 })
                foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift })
                {
                    var pressed = ShortcutService.Canonicalize(ShortcutService.FromKey(key, modifiers));
                    Check(ReferenceEquals(LegacyJingle(decks, selected, key, modifiers), index.FindJingle(pressed, selected)), "Jingle/deck precedence differs from RC1");
                    Check(ReferenceEquals(LegacyAnchor(decks, key, modifiers), index.FindCategoryAnchor(pressed)), "Category anchor precedence differs from RC1");
                    Check(LegacyCategory(decks, key, modifiers).SequenceEqual(index.GetCategoryCandidates(pressed)), "Category union/candidate order differs from RC1");
                }
        var deckIds = decks.Take(2).Select(deck => deck.Id).Append(Guid.NewGuid()).ToArray();
        var jingleIds = decks.SelectMany(deck => deck.Jingles).Where((_, offset) => offset % 3 == 0).Select(jingle => jingle.Id).Append(Guid.NewGuid()).ToArray();
        bool Exists(string path) => path != "missing.wav";
        Check(LegacyPool(decks, deckIds, jingleIds, Exists).SequenceEqual(JingleShortcutIndex.ExistingAudio(index.GetPoolMembers(deckIds, jingleIds), Exists)), "Random pool union, duplicate IDs or first-existing order differs from RC1");
        Check(LegacyPool(decks, [], jingleIds, Exists).SequenceEqual(JingleShortcutIndex.ExistingAudio(index.GetAudioByIds(jingleIds), Exists)), "Follow-up order differs from RC1");
        var idSet = jingleIds.ToHashSet();
        var legacyDeckCandidates = decks.SelectMany(deck => deck.Jingles.Where(jingle => idSet.Contains(jingle.Id)).Select(jingle => new RandomPoolCandidate(jingle, deck.Id)))
            .DistinctBy(candidate => candidate.Jingle.Id).ToArray();
        Check(legacyDeckCandidates.SequenceEqual(index.GetRandomDeckCandidates(jingleIds)), "Random deck attribution/order differs from RC1");
        foreach (var id in jingleIds)
            Check(ReferenceEquals(decks.SelectMany(deck => deck.Jingles).FirstOrDefault(jingle => jingle.Id == id && jingle.HasAudio), index.FindAudioById(id)), "Team ID lookup differs from RC1");
    }

    private static void CheckSelectiveValidation()
    {
        var decks = CreateDecks(553);
        using var index = new JingleShortcutIndex(decks);
        var selected = decks[^1].Jingles[^1];
        var available = new HashSet<string> { selected.FilePath };
        var checkedPaths = new List<string>();
        bool Exists(string path) { checkedPaths.Add(path); return available.Contains(path); }
        var members = index.GetPoolMembers([], new[] { selected.Id });
        Check(JingleShortcutIndex.ExistingAudio(members, Exists).Single() == selected && checkedPaths.SequenceEqual(new[] { selected.FilePath }), "Unrelated files must not be probed");
        available.Clear();
        Check(JingleShortcutIndex.ExistingAudio(members, Exists).Length == 0, "Removing a file must affect the next press without cache invalidation");
        available.Add(selected.FilePath);
        Check(JingleShortcutIndex.ExistingAudio(members, Exists).Single() == selected, "Restoring a file must affect the next press");
        var root = Path.Combine(Path.GetTempPath(), "FloorballDJ-member-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "availability fixture");
        selected.FilePath = root;
        Check(JingleShortcutIndex.ExistingAudio(index.GetAudioByIds(new[] { selected.Id })).Single() == selected, "Real existing-file probe");
        File.Delete(root);
        Check(JingleShortcutIndex.ExistingAudio(index.GetAudioByIds(new[] { selected.Id })).Length == 0, "Real removed-file probe");
    }

    private static void CheckKeyEncoding()
    {
        foreach (var key in Enum.GetValues<Key>())
            for (var modifiers = 0; modifiers < 16; modifiers++)
                Check(ShortcutService.FromKey(key, (ModifierKeys)modifiers) == LegacyKey(key, (ModifierKeys)modifiers), "Key/modifier encoding must match RC1");
        foreach (var stored in new string?[] { null, "", " ", " None ", "N/A", "f6", " F6 ", "Control + F6", "control+f6", "CTRL+F6", "Ctrl + Shift + F6", "+", " Num 1 ", "F\t6" })
            foreach (var key in new[] { Key.F6, Key.OemPlus, Key.NumPad1 })
                foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift })
                    Check(LegacyMatches(stored, key, modifiers) == ShortcutService.MatchesCanonical(stored, ShortcutService.Canonicalize(ShortcutService.FromKey(key, modifiers))), "Shortcut alias/whitespace/disabled sentinel semantics must match RC1");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedIndex(ObservableCollection<Deck> decks)
    {
        var index = new JingleShortcutIndex(decks);
        index.Refresh();
        var weak = new WeakReference(index);
        index.Dispose();
        return weak;
    }

    private static ObservableCollection<Deck> CreateDecks(int count)
    {
        ObservableCollection<Deck> decks = [];
        for (var offset = 0; offset < count; offset++)
        {
            if (offset % 20 == 0) decks.Add(new Deck());
            decks[^1].Jingles.Add(new Jingle
            {
                FilePath = "fixture-" + offset + ".wav", Shortcut = offset % 7 == 0 ? "F6" : "F9",
                CategoryShortcut = offset % 5 == 0 ? "F8" : null, Category = offset % 3 == 0 ? "I" : "i"
            });
        }
        return decks;
    }

    // Frozen RC1 routing contract, independent of the production index/token helpers.
    private static Jingle? LegacyJingle(ObservableCollection<Deck> decks, Deck? selected, Key key, ModifierKeys modifiers)
        => selected?.Jingles.FirstOrDefault(jingle => jingle.HasAudio && LegacyMatches(jingle.Shortcut, key, modifiers)) ??
           decks.Where(deck => deck != selected).SelectMany(deck => deck.Jingles).FirstOrDefault(jingle => jingle.HasAudio && LegacyMatches(jingle.Shortcut, key, modifiers));
    private static Jingle? LegacyAnchor(ObservableCollection<Deck> decks, Key key, ModifierKeys modifiers)
        => decks.SelectMany(deck => deck.Jingles).FirstOrDefault(jingle => jingle.HasAudio && LegacyMatches(jingle.CategoryShortcut, key, modifiers));
    private static Jingle[] LegacyCategory(ObservableCollection<Deck> decks, Key key, ModifierKeys modifiers)
    {
        var anchor = LegacyAnchor(decks, key, modifiers);
        if (anchor is null) return [];
        var named = !string.IsNullOrWhiteSpace(anchor.Category);
        return decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio &&
            ((named && string.Equals(jingle.Category.Trim(), anchor.Category.Trim(), StringComparison.CurrentCultureIgnoreCase)) || LegacyMatches(jingle.CategoryShortcut, key, modifiers))).ToArray();
    }
    private static Jingle[] LegacyPool(ObservableCollection<Deck> decks, IReadOnlyCollection<Guid> deckIds, IReadOnlyCollection<Guid> jingleIds, Func<string, bool> exists)
    {
        var deckSet = deckIds.ToHashSet(); var jingleSet = jingleIds.ToHashSet();
        return decks.SelectMany(deck => deck.Jingles.Where(jingle => jingle.HasAudio && exists(jingle.FilePath) &&
            (deckSet.Contains(deck.Id) || jingleSet.Contains(jingle.Id)))).DistinctBy(jingle => jingle.Id).ToArray();
    }
    private static bool LegacyMatches(string? value, Key key, ModifierKeys modifiers)
    {
        var stored = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (stored?.Equals("None", StringComparison.OrdinalIgnoreCase) == true || stored?.Equals("N/A", StringComparison.OrdinalIgnoreCase) == true) stored = null;
        var pressed = LegacyKey(key, modifiers);
        return stored is not null && pressed is not null &&
            stored.Replace(" ", "", StringComparison.Ordinal).Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase)
                .Equals(pressed.Replace(" ", "", StringComparison.Ordinal).Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }
    private static string? LegacyKey(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var display = key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() :
            key is >= Key.NumPad0 and <= Key.NumPad9 ? $"Num {((int)key - (int)Key.NumPad0)}" : key switch
            {
                Key.OemPlus => "+", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
                Key.Return => "Enter", Key.Back => "Backspace", Key.Next => "Page Down", Key.Prior => "Page Up", _ => key.ToString()
            };
        parts.Add(display);
        return string.Join(" + ", parts);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
