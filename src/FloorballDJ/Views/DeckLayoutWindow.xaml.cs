using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class DeckLayoutWindow : Window
{
    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public DeckLayoutWindow(Deck deck)
    {
        InitializeComponent();
        DeckNameText.Text = deck.Name;
        RowsBox.Text = deck.Rows.ToString();
        ColumnsBox.Text = deck.Columns.ToString();
        Loaded += (_, _) => { RowsBox.Focus(); RowsBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RowsBox.Text, out var rows) || rows is < 1 or > ProjectService.MaximumDeckRows ||
            !int.TryParse(ColumnsBox.Text, out var columns) || columns is < 1 or > ProjectService.MaximumDeckColumns)
        {
            MessageBox.Show(this, $"Rader måste vara mellan 1 och {ProjectService.MaximumDeckRows}. Kolumner måste vara mellan 1 och {ProjectService.MaximumDeckColumns}.", "Kontrollera layouten", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Rows = rows;
        Columns = columns;
        DialogResult = true;
    }
}
