using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class JingleSearchWindow : Window
{
    private sealed record SearchResult(Deck Deck, Jingle Jingle, string DeckName, string Title, int Score);
    private readonly List<SearchResult> _all;
    public Deck? SelectedDeck { get; private set; }
    public Jingle? SelectedJingle { get; private set; }

    public JingleSearchWindow(IEnumerable<Deck> decks)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _all = decks.SelectMany(deck => deck.Jingles.Where(jingle => jingle.HasAudio)
            .Select(jingle => new SearchResult(deck, jingle, deck.Name, jingle.Title, 0))).ToList();
        Loaded += (_, _) => { SearchBox.Focus(); RefreshResults(); };
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshResults();

    private void RefreshResults()
    {
        var query = SearchBox.Text.Trim();
        var results = _all.Select(item => item with { Score = MatchScore(item.Title, query) })
            .Where(item => query.Length == 0 || item.Score < int.MaxValue)
            .OrderBy(item => item.Score).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(100).ToList();
        ResultsList.ItemsSource = results;
        if (results.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private static int MatchScore(string title, string query)
    {
        if (query.Length == 0) return 0;
        var normalizedTitle = title.ToLowerInvariant();
        var normalizedQuery = query.ToLowerInvariant();
        if (normalizedTitle == normalizedQuery) return 0;
        if (normalizedTitle.StartsWith(normalizedQuery)) return 1;
        var contains = normalizedTitle.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (contains >= 0) return 10 + contains;
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(normalizedTitle.Contains)) return 50;
        return Levenshtein(normalizedTitle, normalizedQuery) <= Math.Max(3, normalizedQuery.Length / 3)
            ? 100 + Levenshtein(normalizedTitle, normalizedQuery) : int.MaxValue;
    }

    private static int Levenshtein(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return row[b.Length];
    }

    private void Select_Click(object sender, RoutedEventArgs e) => AcceptSelection();
    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();
    private void ResultsList_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AcceptSelection(); }
    private void AcceptSelection()
    {
        if (ResultsList.SelectedItem is not SearchResult result) return;
        SelectedDeck = result.Deck;
        SelectedJingle = result.Jingle;
        DialogResult = true;
    }
}
