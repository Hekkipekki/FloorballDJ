using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class DeckAppearanceSelectionWindow : Window
{
    private readonly Deck _deck;
    private readonly List<SelectionItem> _items;

    public IReadOnlyList<Jingle> SelectedJingles => _items.Where(item => item.IsSelected).Select(item => item.Jingle).ToArray();

    public DeckAppearanceSelectionWindow(Deck deck)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _deck = deck;
        _items = deck.Jingles.Where(jingle => jingle.HasContent).OrderBy(jingle => jingle.Position).Select(CreateItem).ToList();
        ButtonList.ItemsSource = _items;
        DeckNameText.Text = $"{deck.Name} · markera jinglar och textblock som ska få samma färger.";
        UpdateCount();
    }

    private SelectionItem CreateItem(Jingle jingle)
    {
        var page = _deck.GetPageForPosition(jingle.Position);
        var local = _deck.GetPositionOnPage(jingle.Position);
        var columns = _deck.GetPageColumns(page);
        return new SelectionItem(jingle, $"Sida {page + 1}",
            string.IsNullOrWhiteSpace(jingle.Title) ? $"Rad {local / columns + 1}, kolumn {local % columns + 1}" : jingle.Title);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetSelection(_ => true);
    private void SelectCurrentPage_Click(object sender, RoutedEventArgs e) =>
        SetSelection(item => _deck.GetPageForPosition(item.Jingle.Position) == _deck.ActivePage);
    private void Clear_Click(object sender, RoutedEventArgs e) => SetSelection(_ => false);
    private void SelectionChanged(object sender, RoutedEventArgs e) => UpdateCount();

    private void SetSelection(Func<SelectionItem, bool> selector)
    {
        foreach (var item in _items) item.IsSelected = selector(item);
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (SelectionCountText is not null) SelectionCountText.Text = $"{_items.Count(item => item.IsSelected)} markerade";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJingles.Count == 0)
        {
            MessageBox.Show(this, "Markera minst en knapp.", "Ingen knapp vald", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private sealed class SelectionItem(Jingle jingle, string location, string title) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public Jingle Jingle { get; } = jingle;
        public string Location { get; } = location;
        public string Title { get; } = title;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Raise(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
