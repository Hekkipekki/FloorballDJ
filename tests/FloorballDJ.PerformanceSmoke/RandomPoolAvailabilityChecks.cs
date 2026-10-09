using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;
using FloorballDJ.Views;
using NAudio.Wave;

internal static class RandomPoolAvailabilityChecks
{
    private sealed class Branch(int count) : Exception { internal int Count => count; }

    internal static async Task RunAsync(string root, string? output)
    {
        var cases = CheckSelectionDistribution();
        CheckFreshDuplicateMembers();
        var measurements = Benchmark();
        await CheckActualShortcut(root);
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "random-pool-availability-checks.json"), JsonSerializer.Serialize(new {
                version = "p1.7-random-pool-availability-v1", passed = true, selectionCases = cases, measurements,
                freshAcrossCommands = true, duplicateIdFirstExisting = true, actualHotkeyAndFollowUpSnapshot = true,
                limitation = "Synthetic policy enumeration and silent default-output shortcut fixture. Benchmarks inject 1ms file-probe latency; not measured ProBook/physical-onset improvement."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"PASS: {cases} exhaustive random-policy/availability distributions; one-probe valid large pools, linear missing fallback, fresh duplicate-ID rules and actual shortcut/fade/follow-up snapshot checks.");
    }

    private static int CheckSelectionDistribution()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 4).Select(i => new RandomPoolCandidate(new Jingle(), i < 2 ? a : b)).ToArray();
        var policies = new (bool Variation, RandomDeckRunState? Run, int Limit)[] {
            (false, null, 4), (true, null, 4), (true, new(a, 1), 2), (true, new(a, 2), -50),
            (true, new(a, 4), 4), (true, new(a, 9), 999), (true, new(a, 10), 999) };
        var cases = 0;
        for (var played = 0; played < 16; played++)
        {
            for (var i = 0; i < 4; i++) candidates[i].Jingle.SessionPlayCount = (played >> i) & 1;
            for (var available = 0; available < 16; available++)
                foreach (var track in new[] { false, true })
                    foreach (var previous in candidates.Select(c => (Guid?)c.Jingle.Id).Prepend(null))
                        foreach (var policy in policies)
                        {
                            var valid = candidates.Where((_, i) => (available & (1 << i)) != 0).ToArray();
                            var expected = RandomPoolSelectionService.GetEligibleCandidates(valid, track, previous, policy.Variation, policy.Limit, policy.Run);
                            var expectedIds = expected.Select(c => c.Jingle.Id).ToHashSet();
                            var probabilities = new Dictionary<Guid, double>();
                            void Visit(int[] choices, double weight)
                            {
                                var cursor = 0; var checkedIds = new HashSet<Guid>();
                                try
                                {
                                    var selected = RandomPoolSelectionService.SelectAvailable(candidates, track, previous,
                                        policy.Variation, policy.Limit, policy.Run, candidate => {
                                            Check(checkedIds.Add(candidate.Jingle.Id), "Each candidate is probed at most once during selection");
                                            return valid.Contains(candidate);
                                        }, count => {
                                            if (count == 1) return 0;
                                            if (cursor == choices.Length) throw new Branch(count);
                                            return choices[cursor++];
                                        });
                                    var id = selected?.Jingle.Id ?? Guid.Empty;
                                    probabilities[id] = probabilities.GetValueOrDefault(id) + weight;
                                }
                                catch (Branch branch)
                                {
                                    for (var choice = 0; choice < branch.Count; choice++) Visit([.. choices, choice], weight / branch.Count);
                                }
                            }
                            Visit([], 1);
                            if (expected.Length == 0) Check(probabilities.Count == 1 && Math.Abs(probabilities[Guid.Empty] - 1) < 1e-9, "Entirely missing pools return no selection");
                            else Check(probabilities.Keys.ToHashSet().SetEquals(expectedIds) && probabilities.Values.All(p => Math.Abs(p - 1d / expected.Length) < 1e-9),
                                "Every availability/session/previous/variation policy has exactly the legacy uniform eligible distribution");
                            cases++;
                        }
        }
        return cases;
    }

    private static void CheckFreshDuplicateMembers()
    {
        var id = Guid.NewGuid();
        var missing = new Jingle { Id = id, FilePath = "missing" };
        var existing = new Jingle { Id = id, FilePath = "existing" };
        var members = new[] { missing, existing };
        var paths = new HashSet<string> { "existing" }; var probes = 0;
        bool Exists(string path) { probes++; return paths.Contains(path); }
        var fresh = new JingleShortcutIndex.FreshPool(members, Exists);
        Check(ReferenceEquals(fresh.FindExisting(id), existing) && probes == 2, "Duplicate ID uses the first existing membership occurrence");
        Check(ReferenceEquals(fresh.FindExisting(id), existing) && probes == 2, "No repeated probe within one command");
        paths.Clear();
        Check(new JingleShortcutIndex.FreshPool(members, Exists).FindExisting(id) is null, "The next command sees file deletion");
        paths.Add("missing");
        Check(ReferenceEquals(new JingleShortcutIndex.FreshPool(members, Exists).FindExisting(id), missing), "The next command sees restore and first-occurrence change");
        // Preserve the index's original first-global-ID deck/jingle attribution.
        var decks = new ObservableCollection<Deck> { new() { Jingles = [missing] }, new() { Jingles = [existing] } };
        using var index = new JingleShortcutIndex(decks);
        var candidate = index.GetRandomDeckCandidates([id]).Single();
        fresh = new JingleShortcutIndex.FreshPool([existing], _ => true);
        Check(ReferenceEquals(RandomPoolSelectionService.SelectAvailable([candidate], true, null, false, 4, null,
            c => fresh.FindExisting(c.Jingle.Id) is not null)!.Jingle, missing), "Duplicate-ID deck attribution remains identical to legacy selection");
    }

    private static object[] Benchmark()
    {
        var deck = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 248).Select(i => new RandomPoolCandidate(new Jingle { FilePath = "probe-" + i }, deck)).ToArray();
        var rows = new List<object>();
        foreach (var legacy in new[] { true, false })
            for (var trial = 0; trial < 6; trial++)
            {
                var probes = 0;
                bool Exists(string _)
                {
                    probes++; var start = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1) Thread.SpinWait(64);
                    return true;
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
                if (legacy)
                {
                    var valid = JingleShortcutIndex.ExistingAudio(candidates.Select(c => c.Jingle), Exists);
                    var ids = valid.Select(j => j.Id).ToHashSet();
                    var eligible = RandomPoolSelectionService.GetEligibleCandidates(candidates.Where(c => ids.Contains(c.Jingle.Id)).ToArray(), true, null, false, 4, null);
                    _ = eligible[Random.Shared.Next(eligible.Length)];
                }
                else
                {
                    var fresh = new JingleShortcutIndex.FreshPool(candidates.Select(c => c.Jingle), Exists);
                    Check(RandomPoolSelectionService.SelectAvailable(candidates, true, null, false, 4, null,
                        c => fresh.FindExisting(c.Jingle.Id) is not null) is not null, "Optimized measured pool selects a playable candidate");
                }
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                rows.Add(new { legacy, trial, probes, elapsedMs = ms, callerBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
                Check(probes == (legacy ? 248 : 1), "Valid large pool checks one file instead of all 248");
            }
        var missingProbes = 0;
        Check(RandomPoolSelectionService.SelectAvailable(candidates, true, null, true, 4, new(deck, 4), _ => { missingProbes++; return false; }) is null && missingProbes == 248,
            "Entirely missing large pool terminates after a linear number of probes");
        return rows.ToArray();
    }

    private static async Task CheckActualShortcut(string root)
    {
        var media = Path.Combine(root, "availability-silent.wav");
        using (var writer = new WaveFileWriter(media, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 4], 0, 48000 * 4 * 4);
        var probes = 0; var followExists = true; var primaryExists = true;
        bool Exists(string path) { probes++; return path == media && primaryExists || path == "follow-up-fixture" && followExists; }
        var window = new MainWindow(new LicenseService(), shutdownApplicationOnClose: false, randomFileExists: Exists);
        var vm = (MainViewModel)window.DataContext;
        using var source = new HwndSource(new HwndSourceParameters("availability-key-fixture") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = new System.Windows.Controls.Border();
        try
        {
            var members = Enumerable.Range(0, 248).Select(_ => new Jingle { FilePath = media, DurationSeconds = 4, FadeInOverrideSeconds = 0, FadeOutOverrideSeconds = .03 }).ToArray();
            vm.Decks[0].Jingles.Clear(); foreach (var jingle in members) vm.Decks[0].Jingles.Add(jingle);
            var follow = new Jingle { FilePath = "follow-up-fixture" }; vm.Decks[0].Jingles.Add(follow);
            vm.Settings.RandomPoolSetups.Clear();
            vm.Settings.RandomPoolProfiles = [new RandomPoolProfile { Shortcut = "F6", JingleIds = members.Select(j => j.Id).ToList(), FollowUpJingleIds = [follow.Id], FollowUpFadeOutSeconds = 0 }];
            vm.Audio.Configure(null, null, -60, -12, 0, .03);
            Task Trigger() => Program.DispatchKeyAsync(window, source, Key.F6);
            await Trigger(); Check(probes == 2, "Actual 248-member route checks one primary and the one start-time follow-up");
            Check(((Guid[])typeof(MainWindow).GetField("_pendingRandomFollowUpIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).SequenceEqual([follow.Id]), "Available follow-up is retained at start");
            await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var before = probes; await Trigger(); Check(probes - before == 1, "Repeated active key freshly checks active membership and fades without scanning unused follow-ups");
            vm.Audio.StopAll(notify: false); await Task.Delay(80);
            followExists = false; before = probes; await Trigger(); Check(probes - before <= 3, "A subsequent command remains bounded and fresh");
            Check(((Guid[])typeof(MainWindow).GetField("_pendingRandomFollowUpIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Length == 0, "A follow-up missing at start is excluded from pending IDs");
            vm.Audio.StopAll(notify: false); await Task.Delay(80);
            var fallback = vm.Decks[1].Jingles[0]; fallback.FilePath = media; fallback.CategoryShortcut = "F6"; fallback.FadeInOverrideSeconds = 0;
            primaryExists = false; before = probes; await Trigger();
            await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(probes - before == 248 && vm.NowPlaying.JingleId == fallback.Id,
                "All missing members are freshly exhausted and the actual category shortcut falls through");
            vm.Audio.StopAll(notify: false); await Task.Delay(80);
            primaryExists = true; before = probes; await Trigger();
            await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(probes - before <= 3 && members.Any(j => j.Id == vm.NowPlaying.JingleId), "Restored membership immediately regains shortcut priority");
        }
        finally { vm.Audio.StopAll(notify: false); window.Close(); await vm.FlushSavesAsync(); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
