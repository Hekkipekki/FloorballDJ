using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class AutoplayView
{
    internal Func<UnsavedProfileChoice>? ConfirmPlaylistReplacement { get; set; }
    internal Func<Task<bool>>? SavePlaylistForTest { get; init; }

    internal async Task<bool> ConfirmReplacementAsync()
    {
        CancelPlaylistPreparation();
        var vm = ViewModel;
        if (vm.PlaybackQueue.Count == 0) return true;
        var project = vm.Project;
        var slot = vm.EditingQueueIndex;
        var revision = vm.QueueRevision;
        var choice = ConfirmPlaylistReplacement?.Invoke();
        if (choice is null)
        {
            var dialog = new UnsavedProfileWindow(vm.EditingPlaylistTitle, playlist: true) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            choice = dialog.Choice;
        }
        if (choice == UnsavedProfileChoice.Cancel) return false;
        if (choice == UnsavedProfileChoice.Save && !await SavePlaylistAsync()) return false;
        // Saving can yield. Never discard edits made during that save.
        return ReferenceEquals(DataContext, vm) && ReferenceEquals(project, vm.Project) &&
               slot == vm.EditingQueueIndex && revision == vm.QueueRevision;
    }

    internal async Task<bool> NewPlaylistAsync()
    {
        if (!await ConfirmReplacementAsync()) return false;
        ViewModel.ReplaceQueue([]);
        ViewModel.QueuePlaylistPath = null;
        ViewModel.QueueLoopEnabled = true;
        ViewModel.QueueShuffleEnabled = false;
        return true;
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e) => await NewPlaylistAsync();
    private async void ClearPlaylist_Click(object sender, RoutedEventArgs e) => await ClearPlaylistAsync();
    internal async Task<bool> ClearPlaylistAsync()
    {
        if (!await ConfirmReplacementAsync()) return false;
        ViewModel.ReplaceQueue([]);
        return true;
    }
}
