using System.Text;
using System.Windows;
using System.Windows.Controls;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class HelpWindow : Window
{
    private readonly Dictionary<string, ScrollViewer> _pages = new(StringComparer.OrdinalIgnoreCase);
    private bool _changingSelection;

    public HelpWindow()
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _pages.Add("quick", QuickStartPage);
        _pages.Add("playback", PlaybackPage);
        _pages.Add("decks", DecksPage);
        _pages.Add("autoplay", AutoplayPage);
        _pages.Add("random", RandomPoolsPage);
        _pages.Add("audio", AudioPage);
        _pages.Add("builder", JingleBuilderPage);
        _pages.Add("properties", PropertiesPage);
        _pages.Add("team", TeamDeckPage);
        _pages.Add("profiles", ProfilesPage);
        _pages.Add("shortcuts", ShortcutsPage);
        _pages.Add("license", LicensingPage);
        _pages.Add("troubleshooting", TroubleshootingPage);
        HelpNavigation.SelectedIndex = 0;
    }

    private IEnumerable<ListBox> NavigationLists => [HelpNavigation, AdvancedNavigation, SupportNavigation];

    private void HelpNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || sender is not ListBox list ||
            list.SelectedItem is not ListBoxItem { Tag: string pageKey }) return;
        _changingSelection = true;
        foreach (var other in NavigationLists.Where(other => !ReferenceEquals(other, list))) other.SelectedIndex = -1;
        SearchResultsList.SelectedIndex = -1;
        _changingSelection = false;
        ShowPage(pageKey);
    }

    private void ShowPage(string pageKey)
    {
        if (!_pages.TryGetValue(pageKey, out var selectedPage)) return;
        foreach (var page in _pages.Values) page.Visibility = ReferenceEquals(page, selectedPage)
            ? Visibility.Visible : Visibility.Collapsed;
        selectedPage.ScrollToTop();
    }

    private void HelpSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        var query = HelpSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            NavigationSections.Visibility = Visibility.Visible;
            SearchResultsList.ItemsSource = null;
            return;
        }

        var results = _pages.Select(pair => new
            {
                pair.Key,
                Page = pair.Value,
                Title = GetNavigationTitle(pair.Key),
                Text = ExtractSearchText(pair.Value)
            })
            .Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                           item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new HelpSearchResult(item.Key, item.Title))
            .ToArray();
        SearchStatusText.Text = LanguageService.IsEnglish
            ? $"{results.Length} {(results.Length == 1 ? "section" : "sections")} found"
            : $"{results.Length} {(results.Length == 1 ? "avsnitt" : "avsnitt")} hittades";
        SearchResultsList.ItemsSource = results;
        NavigationSections.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
    }

    private void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not HelpSearchResult result) return;
        _changingSelection = true;
        foreach (var list in NavigationLists) list.SelectedIndex = -1;
        _changingSelection = false;
        ShowPage(result.PageKey);
    }

    private string GetNavigationTitle(string pageKey)
    {
        foreach (var item in NavigationLists.SelectMany(list => list.Items.OfType<ListBoxItem>()))
        {
            if (!string.Equals(item.Tag as string, pageKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Content is TextBlock label) return label.Text;
        }
        return pageKey;
    }

    private static string ExtractSearchText(DependencyObject root)
    {
        var builder = new StringBuilder();
        var visited = new HashSet<DependencyObject>();
        AppendSearchText(root, builder, visited);
        return builder.ToString();
    }

    private static void AppendSearchText(DependencyObject root, StringBuilder builder, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root)) return;
        if (root is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) builder.Append(' ').Append(text.Text);
        if (root is GroupBox { Header: string header }) builder.Append(' ').Append(header);
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is string value) builder.Append(' ').Append(value);
            else if (child is DependencyObject dependencyChild) AppendSearchText(dependencyChild, builder, visited);
        }
    }
}

public sealed record HelpSearchResult(string PageKey, string Title);
