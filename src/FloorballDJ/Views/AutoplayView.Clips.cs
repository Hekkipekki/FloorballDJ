using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloorballDJ.Models;

namespace FloorballDJ.Views;

public partial class AutoplayView
{
    private static ListBox? ParentQueue(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBox list) return list;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
    private void RawFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Jingle song } box || ParentQueue(box) is not { } list) return;
        var slot = ReferenceEquals(list, RightQueueList) ? 1 : 0;
        var options = ViewModel.GetQueueClipOptions(slot, song);
        // An earlier custom end may precede the deck start when switching back.
        var end = options.EndSeconds is { } value && box.IsChecked != true && value <= song.StartSeconds ? null : options.EndSeconds;
        ViewModel.SetQueueClipOptions(slot, song, box.IsChecked == true, end);
        e.Handled = true;
    }
    private void Queue_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as Jingle ?? list.SelectedItem as Jingle;
        if (item is null) { e.Handled = true; return; }
        var slot = ReferenceEquals(list, RightQueueList) ? 1 : 0;
        ViewModel.EditingQueueIndex = slot;
        list.SelectedItem = item;
        list.ContextMenu.Tag = (slot, item);
    }
    private async void EditQueueEnd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || ItemsControl.ItemsControlFromItemContainer(menu) is not ContextMenu { Tag: ValueTuple<int, Jingle> target }) return;
        var (slot, song) = target;
        var vm = ViewModel; var project = vm.Project;
        try
        {
            var metadata = await vm.Metadata.ReadAsync([song.FilePath], vm.ProfileWorkCancellation);
            if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(project, vm.Project)) return;
            var duration = metadata[0].DurationSeconds ?? song.DurationSeconds;
            if (duration <= 0) return;
            var options = vm.GetQueueClipOptions(slot, song);
            var dialog = new AutoplayClipWindow(song.Title, options.UseRawFile ? 0 : song.StartSeconds, duration, options.EndSeconds) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (!ReferenceEquals(project, vm.Project) || !(slot == 0 ? vm.LeftPlaybackQueue : vm.RightPlaybackQueue).Contains(song)) return;
            vm.SetQueueClipOptions(slot, song, options.UseRawFile, dialog.EndSeconds, duration);
        }
        catch (OperationCanceledException) { }
    }
}
