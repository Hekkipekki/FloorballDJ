using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class MainWindow
{
    private Deck? _lastAutoplayPickerDeck;
    private Deck? _deckBeforeAutoplay;

    private void AutoplaySongPicker_Requested(object? sender, EventArgs e)
    {
        if (!ViewModel.AutoplayModeActive) return;
        EmbeddedAutoplay.CancelPlaylistPreparation();
        ViewModel.AutoplaySongPickerActive = true;
        EmbeddedAutoplay.Visibility = Visibility.Collapsed;
        RestoreDeckAfterAutoplay(_lastAutoplayPickerDeck);
        FinishSongPickerButton.Focus();
    }

    private void FinishSongPicker_Click(object sender, RoutedEventArgs e) => FinishAutoplaySongPicker();

    private void FinishAutoplaySongPicker()
    {
        if (!ViewModel.AutoplaySongPickerActive) return;
        _lastAutoplayPickerDeck = ViewModel.SelectedDeck;
        ViewModel.AutoplaySongPickerActive = false;
        DeckTabsControl.SelectedIndex = -1;
        EmbeddedAutoplay.Visibility = Visibility.Visible;
        ((Button)EmbeddedAutoplay.FindName("ChooseDeckSongsButton")).Focus();
    }

    private void ToggleAutoplayPickerSong(Jingle jingle)
    {
        if (!jingle.HasAudio || jingle.IsTextBlock) return;
        var queued = ViewModel.PlaybackQueue.Where(item => item.Id == jingle.Id &&
            string.Equals(item.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (queued.Length == 0) ViewModel.AddToQueue(jingle);
        else foreach (var item in queued) ViewModel.RemoveFromQueue(item);
        ViewModel.Status = string.Format(LanguageService.Translate(queued.Length == 0 ? "Tillagd i Autoplay-kön: {0}" : "Borttagen från Autoplay-kön: {0}"), jingle.Title);
    }

    private void SelectPickerPage_Click(object sender, RoutedEventArgs e) => SetPickerSelection(false, true);
    private void DeselectPickerPage_Click(object sender, RoutedEventArgs e) => SetPickerSelection(false, false);
    private void SelectPickerDeck_Click(object sender, RoutedEventArgs e) => SetPickerSelection(true, true);
    private void DeselectPickerDeck_Click(object sender, RoutedEventArgs e) => SetPickerSelection(true, false);

    internal void SetPickerSelection(bool wholeDeck, bool selected)
    {
        if (!ViewModel.AutoplaySongPickerActive || ViewModel.SelectedDeck is not { } deck) return;
        var songs = wholeDeck ? deck.Jingles.AsEnumerable() : deck.Jingles.Skip(deck.GetPageStartIndex(deck.ActivePage)).Take(deck.GetPageCapacity(deck.ActivePage));
        foreach (var song in songs.Where(song => song.HasAudio && !song.IsTextBlock).ToArray())
        {
            if (selected) ViewModel.AddToQueue(song);
            else
                foreach (var item in ViewModel.PlaybackQueue.Where(item => item.Id == song.Id && string.Equals(item.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase)).ToArray()) ViewModel.RemoveFromQueue(item);
        }
    }

    private void RestoreDeckAfterAutoplay(Deck? preferred = null)
    {
        var available = DeckTabsControl.Items.Cast<Deck>().ToArray();
        var deck = available.FirstOrDefault(item => ReferenceEquals(item, preferred ?? ViewModel.SelectedDeck))
            ?? available.FirstOrDefault(item => ReferenceEquals(item, _deckBeforeAutoplay)) ?? available.FirstOrDefault();
        ViewModel.SelectedDeck = deck;
        DeckTabsControl.SetCurrentValue(Selector.SelectedItemProperty, deck);
    }

    private bool CanBrowseDecks() => ViewModel.AutoplaySongPickerActive || TryLeaveAutoplay();

    private void Jingle_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ViewModel.AutoplaySongPickerActive) e.Handled = true;
    }
}
