using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

/// <summary>UI-owned lookup for the current profile. Edits invalidate one coalesced rebuild.</summary>
internal sealed class JingleShortcutIndex : IDisposable
{
    private readonly record struct Entry(Deck Deck, Jingle Jingle, int Order);
    private ObservableCollection<Deck>? _decks;
    private readonly HashSet<Deck> _observedDecks = [];
    private readonly HashSet<ObservableCollection<Jingle>> _observedCollections = [];
    private readonly HashSet<Jingle> _observedJingles = [];
    private Dictionary<string, List<Entry>> _shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<Entry>> _categoryShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<Entry>> _categories = new(StringComparer.CurrentCultureIgnoreCase);
    private Dictionary<Guid, List<Entry>> _deckIds = [];
    private Dictionary<Guid, List<Entry>> _jingleIds = [];
    private CultureInfo? _culture;
    private bool _dirty = true;
    private bool _subscriptionsDirty = true;
    private bool _disposed;

    internal JingleShortcutIndex(ObservableCollection<Deck> decks) => SetDecks(decks);

    internal void SetDecks(ObservableCollection<Deck> decks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_decks, decks)) return;
        if (_decks is not null) _decks.CollectionChanged -= StructureChanged;
        DetachMembers();
        _decks = decks;
        _decks.CollectionChanged += StructureChanged;
        _shortcuts = new(StringComparer.OrdinalIgnoreCase);
        _categoryShortcuts = new(StringComparer.OrdinalIgnoreCase);
        _categories = new(StringComparer.CurrentCultureIgnoreCase);
        _deckIds = [];
        _jingleIds = [];
        _dirty = _subscriptionsDirty = true;
    }

    internal void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirty && Equals(_culture, CultureInfo.CurrentCulture)) return;
        var performance = PerformanceDiagnostics.BeginOperation("ShortcutIndexRebuildRequested");
        using var duration = performance.Measure("ShortcutIndexRebuild");
        if (_subscriptionsDirty)
        {
            // Reset/move/bulk replacement is reconciled once, not on every inserted cart.
            DetachMembers();
            foreach (var deck in _decks!)
            {
                if (_observedDecks.Add(deck)) deck.PropertyChanged += DeckChanged;
                if (_observedCollections.Add(deck.Jingles)) deck.Jingles.CollectionChanged += StructureChanged;
                foreach (var jingle in deck.Jingles)
                    if (_observedJingles.Add(jingle)) jingle.PropertyChanged += JingleChanged;
            }
            _subscriptionsDirty = false;
        }
        _shortcuts.Clear();
        _categoryShortcuts.Clear();
        _culture = CultureInfo.CurrentCulture;
        _categories = new(StringComparer.Create(_culture, true));
        _deckIds.Clear();
        _jingleIds.Clear();
        var order = 0;
        foreach (var deck in _decks!)
            foreach (var jingle in deck.Jingles)
            {
                var entry = new Entry(deck, jingle, order++);
                Add(_jingleIds, jingle.Id, entry);
                if (!jingle.HasAudio) continue;
                Add(_deckIds, deck.Id, entry);
                Add(_shortcuts, ShortcutService.Canonicalize(jingle.Shortcut), entry);
                Add(_categoryShortcuts, ShortcutService.Canonicalize(jingle.CategoryShortcut), entry);
                if (!string.IsNullOrWhiteSpace(jingle.Category)) Add(_categories, jingle.Category.Trim(), entry);
            }
        performance.Mark("ShortcutIndexSlotCount", order);
        _dirty = false;
    }

    internal Jingle? FindJingle(string? pressed, Deck? selectedDeck)
    {
        Refresh();
        if (pressed is null) return null;
        // Preserve the previous selected-deck rule even for a transient/external selection.
        if (selectedDeck is not null && !_observedDecks.Contains(selectedDeck))
            foreach (var jingle in selectedDeck.Jingles)
                if (jingle.HasAudio && ShortcutService.MatchesCanonical(jingle.Shortcut, pressed)) return jingle;
        if (!_shortcuts.TryGetValue(pressed, out var matches)) return null;
        foreach (var entry in matches)
            if (ReferenceEquals(entry.Deck, selectedDeck)) return entry.Jingle;
        return matches[0].Jingle;
    }

    internal Jingle? FindCategoryAnchor(string? pressed)
    {
        Refresh();
        return pressed is not null && _categoryShortcuts.TryGetValue(pressed, out var matches) ? matches[0].Jingle : null;
    }

    internal Jingle[] GetCategoryCandidates(string? pressed)
    {
        Refresh();
        if (pressed is null || !_categoryShortcuts.TryGetValue(pressed, out var shortcuts)) return [];
        var anchor = shortcuts[0].Jingle;
        List<Entry>? named = null;
        if (!string.IsNullOrWhiteSpace(anchor.Category)) _categories.TryGetValue(anchor.Category.Trim(), out named);
        // Both lists follow current deck/cart order. Merge the union without changing which
        // duplicate ID the existing random selector sees first, or its candidate ordering.
        var result = new List<Jingle>(shortcuts.Count + (named?.Count ?? 0));
        var shortcutIndex = 0;
        var namedIndex = 0;
        while (shortcutIndex < shortcuts.Count || namedIndex < (named?.Count ?? 0))
        {
            if (named is null || namedIndex >= named.Count ||
                (shortcutIndex < shortcuts.Count && shortcuts[shortcutIndex].Order < named[namedIndex].Order))
                result.Add(shortcuts[shortcutIndex++].Jingle);
            else if (shortcutIndex >= shortcuts.Count || named[namedIndex].Order < shortcuts[shortcutIndex].Order)
                result.Add(named[namedIndex++].Jingle);
            else
            {
                result.Add(shortcuts[shortcutIndex++].Jingle);
                namedIndex++;
            }
        }
        return [.. result];
    }

    internal Jingle[] GetPoolMembers(IEnumerable<Guid> deckIds, IEnumerable<Guid> jingleIds)
    {
        Refresh();
        var entries = new List<Entry>();
        var seen = new HashSet<int>();
        foreach (var id in deckIds)
            if (_deckIds.TryGetValue(id, out var deck))
                foreach (var entry in deck)
                    if (seen.Add(entry.Order)) entries.Add(entry);
        foreach (var id in jingleIds)
            if (_jingleIds.TryGetValue(id, out var jingles))
                foreach (var entry in jingles)
                    if (entry.Jingle.HasAudio && seen.Add(entry.Order)) entries.Add(entry);
        entries.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        return entries.Select(entry => entry.Jingle).ToArray();
    }

    internal Jingle[] GetAudioByIds(IEnumerable<Guid> jingleIds) => GetPoolMembers([], jingleIds);

    internal Jingle? FindAudioById(Guid id)
    {
        Refresh();
        return _jingleIds.TryGetValue(id, out var jingles) ? jingles.FirstOrDefault(entry => entry.Jingle.HasAudio).Jingle : null;
    }

    internal RandomPoolCandidate[] GetRandomDeckCandidates(IEnumerable<Guid> jingleIds)
    {
        Refresh();
        var entries = new List<Entry>();
        var seen = new HashSet<Guid>();
        foreach (var id in jingleIds)
            if (seen.Add(id) && _jingleIds.TryGetValue(id, out var jingles)) entries.Add(jingles[0]);
        entries.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        return entries.Select(entry => new RandomPoolCandidate(entry.Jingle, entry.Deck.Id)).ToArray();
    }

    // Validate only potential members, then keep the first existing occurrence per ID,
    // matching the prior scan's ordering even when a profile contains duplicate IDs.
    internal static Jingle[] ExistingAudio(IEnumerable<Jingle> members, Func<string, bool>? fileExists = null)
        => members.Where(jingle => (fileExists ?? File.Exists)(jingle.FilePath)).DistinctBy(jingle => jingle.Id).ToArray();

    // Command-owned availability only. Preserve the first existing occurrence
    // per ID for malformed/legacy duplicate IDs; never retain status across keys.
    internal sealed class FreshPool(IEnumerable<Jingle> members, Func<string, bool> fileExists)
    {
        private readonly ILookup<Guid, Jingle> _members = members.ToLookup(jingle => jingle.Id);
        private readonly Dictionary<Guid, Jingle?> _checked = [];
        internal int FileProbes { get; private set; }
        internal bool CheckFile(string path) { FileProbes++; return fileExists(path); }
        internal Jingle? FindExisting(Guid id)
        {
            if (_checked.TryGetValue(id, out var known)) return known;
            var found = _members[id].FirstOrDefault(jingle => CheckFile(jingle.FilePath));
            _checked.Add(id, found);
            return found;
        }
    }

    private static void Add(Dictionary<string, List<Entry>> map, string? key, Entry entry)
    {
        if (key is null) return;
        if (!map.TryGetValue(key, out var entries)) map[key] = entries = [];
        entries.Add(entry);
    }

    private static void Add(Dictionary<Guid, List<Entry>> map, Guid key, Entry entry)
    {
        if (!map.TryGetValue(key, out var entries)) map[key] = entries = [];
        entries.Add(entry);
    }

    private void StructureChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // NotifyJingleChanged also replaces a slot with the same object to refresh WPF.
        // Its relevant property notifications already invalidate us; appearance-only
        // refreshes must not rebuild a whole profile for every selected button.
        if (args.Action == NotifyCollectionChangedAction.Replace && args.NewItems is not null &&
            args.OldItems is not null && args.NewItems.Count == args.OldItems.Count)
        {
            var identical = true;
            for (var index = 0; index < args.NewItems.Count; index++)
                identical &= ReferenceEquals(args.NewItems[index], args.OldItems[index]);
            if (identical) return;
        }
        _dirty = _subscriptionsDirty = true;
    }

    private void DeckChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(Deck.Jingles))
            _dirty = _subscriptionsDirty = true;
        else if (args.PropertyName == nameof(Deck.Id)) _dirty = true;
    }

    private void JingleChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(Jingle.Shortcut) or
            nameof(Jingle.CategoryShortcut) or nameof(Jingle.Category) or nameof(Jingle.FilePath) or nameof(Jingle.Id)) _dirty = true;
    }

    private void DetachMembers()
    {
        foreach (var jingle in _observedJingles) jingle.PropertyChanged -= JingleChanged;
        foreach (var collection in _observedCollections) collection.CollectionChanged -= StructureChanged;
        foreach (var deck in _observedDecks) deck.PropertyChanged -= DeckChanged;
        _observedJingles.Clear();
        _observedCollections.Clear();
        _observedDecks.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_decks is not null) _decks.CollectionChanged -= StructureChanged;
        DetachMembers();
        _decks = null;
        _shortcuts.Clear();
        _categoryShortcuts.Clear();
        _categories.Clear();
        _deckIds.Clear();
        _jingleIds.Clear();
    }
}
