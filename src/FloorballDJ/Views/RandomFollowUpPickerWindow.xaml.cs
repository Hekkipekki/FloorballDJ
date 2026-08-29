using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class RandomFollowUpPickerWindow : Window
{
    private readonly List<FollowUpChoice> _allItems;
    private bool _descending;
    public ObservableCollection<FollowUpChoice> VisibleItems { get; } = [];
    public ObservableCollection<string> DeckNames { get; } = [];
    public IReadOnlyCollection<Guid> SelectedJingleIds => _allItems.Where(item => item.IsSelected).Select(item => item.JingleId).ToArray();

    public RandomFollowUpPickerWindow(FloorballProject project, IEnumerable<Guid> selectedIds)
    {
        InitializeComponent();
        WindowPlacementService.FitToOwnerMonitor(this);
        DataContext = this;
        var selected = selectedIds.ToHashSet();
        _allItems = project.Decks.SelectMany(deck => deck.Jingles
                .Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath))
                .Select(jingle => new FollowUpChoice(jingle, deck.Name, selected.Contains(jingle.Id))))
            .ToList();
        DeckNames.Add(LanguageService.IsEnglish ? "All" : "Alla");
        foreach (var deckName in project.Decks.Select(deck => deck.Name).Where(name =>
                     _allItems.Any(item => string.Equals(item.DeckName, name, StringComparison.CurrentCultureIgnoreCase))))
            DeckNames.Add(deckName);
        foreach (var item in _allItems) item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FollowUpChoice.IsSelected)) RefreshCount();
        };
        RefreshItems();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems();
    private void DeckTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshItems();
    private void SortAscending_Click(object sender, RoutedEventArgs e) { _descending = false; RefreshItems(); }
    private void SortDescending_Click(object sender, RoutedEventArgs e) { _descending = true; RefreshItems(); }

    private void RefreshItems()
    {
        var query = SearchBox?.Text.Trim() ?? "";
        var deck = DeckTabs?.SelectedIndex > 0 ? DeckTabs.SelectedItem as string : null;
        IEnumerable<FollowUpChoice> items = _allItems.Where(item =>
            (deck is null || string.Equals(item.DeckName, deck, StringComparison.CurrentCultureIgnoreCase)) && (query.Length == 0 ||
            item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.DeckName.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        items = _descending
            ? items.OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            : items.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase);
        VisibleItems.Clear();
        foreach (var item in items) VisibleItems.Add(item);
        RefreshCount();
    }

    private void RefreshCount()
    {
        if (SelectedCountText is not null)
            SelectedCountText.Text = LanguageService.IsEnglish
                ? $"{SelectedJingleIds.Count} follow-up songs selected"
                : $"{SelectedJingleIds.Count} följdlåtar valda";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public sealed class FollowUpChoice : INotifyPropertyChanged
{
    private bool _isSelected;
    public FollowUpChoice(Jingle jingle, string deckName, bool selected)
    {
        JingleId = jingle.Id;
        Title = string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title;
        DeckName = deckName;
        var duration = Math.Max(0, jingle.EndSeconds.GetValueOrDefault(jingle.DurationSeconds) - jingle.StartSeconds);
        DurationDisplay = TimeSpan.FromSeconds(duration).ToString(duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        _isSelected = selected;
    }
    public Guid JingleId { get; }
    public string Title { get; }
    public string DeckName { get; }
    public string DurationDisplay { get; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
