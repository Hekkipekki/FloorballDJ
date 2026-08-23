using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class TeamJinglePickerWindow : Window, INotifyPropertyChanged
{
    private readonly List<TeamJingleSearchItem> _allChoices;
    private string _resultText = "";

    public ObservableCollection<TeamJingleSearchItem> VisibleChoices { get; } = [];
    public string ResultText { get => _resultText; private set { _resultText = value; Raise(); } }
    public Guid? SelectedJingleId { get; private set; }
    public double? SelectedStartSeconds { get; private set; }

    public TeamJinglePickerWindow(FloorballProject project, Guid? currentJingleId)
    {
        _allChoices = project.Decks.SelectMany(deck => deck.Jingles
                .Where(jingle => jingle.HasAudio)
                .Select(jingle => new TeamJingleSearchItem(deck.Name, jingle)))
            .OrderBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        InitializeComponent();
        DataContext = this;
        ApplyFilter("");
        if (currentJingleId is Guid current)
        {
            var selected = VisibleChoices.FirstOrDefault(choice => choice.JingleId == current);
            if (selected is not null)
            {
                ResultsList.SelectedItem = selected;
                ResultsList.ScrollIntoView(selected);
            }
        }
        Loaded += (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); };
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = terms.Length == 0
            ? _allChoices
            : _allChoices.Where(choice => terms.All(term => choice.SearchText.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToList();
        VisibleChoices.Clear();
        foreach (var match in matches) VisibleChoices.Add(match);
        ResultsList.SelectedIndex = VisibleChoices.Count > 0 ? 0 : -1;
        ResultText = $"{VisibleChoices.Count} av {_allChoices.Count} ljud";
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && VisibleChoices.Count > 0)
        {
            ResultsList.Focus();
            ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { ConfirmSelection(); e.Handled = true; }
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ConfirmSelection(); e.Handled = true; }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();
    private void Select_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (ResultsList.SelectedItem is not TeamJingleSearchItem selected) return;
        SelectedJingleId = selected.JingleId;
        SelectedStartSeconds = selected.StartSeconds;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedJingleId = null;
        SelectedStartSeconds = null;
        DialogResult = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TeamJingleSearchItem
{
    public Guid JingleId { get; }
    public string DeckName { get; }
    public string Title { get; }
    public string FileName { get; }
    public double StartSeconds { get; }
    public string SearchText { get; }

    public TeamJingleSearchItem(string deckName, Jingle jingle)
    {
        JingleId = jingle.Id;
        DeckName = deckName;
        Title = string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title;
        FileName = Path.GetFileName(jingle.FilePath);
        StartSeconds = jingle.StartSeconds;
        SearchText = $"{DeckName} {Title} {FileName}";
    }
}
