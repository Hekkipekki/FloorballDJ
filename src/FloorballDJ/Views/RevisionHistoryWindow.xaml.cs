using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;

namespace FloorballDJ.Views;

public partial class RevisionHistoryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<ProjectRevision> _revisions = [];

    public RevisionHistoryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        RevisionList.ItemsSource = _revisions;
        Loaded += RevisionHistoryWindow_Loaded;
    }

    private async void RevisionHistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var revision in await _viewModel.GetRevisionsAsync()) _revisions.Add(revision);
            StatusText.Text = _revisions.Count == 0
                ? "Det finns ännu inga äldre revisioner. En punkt skapas nästa gång projektet ändras."
                : $"{_revisions.Count} återställningspunkter hittades. Senaste visas överst.";
            if (_revisions.Count > 0) RevisionList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Historiken kunde inte läsas: {ex.Message}";
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreSelectedAsync();

    private async void RevisionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RevisionList.SelectedItem is not null) await RestoreSelectedAsync();
    }

    private async Task RestoreSelectedAsync()
    {
        if (RevisionList.SelectedItem is not ProjectRevision revision) return;
        if (MessageBox.Show(this,
                $"Återställ profilen till {revision.TimestampText}?\n\nDen nuvarande versionen sparas först i historiken.",
                "Återställ revision", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.RestoreRevisionAsync(revision.Path);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Revisionen kunde inte återställas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
