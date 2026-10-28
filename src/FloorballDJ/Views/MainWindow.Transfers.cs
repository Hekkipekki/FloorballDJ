namespace FloorballDJ.Views;

public partial class MainWindow
{
    private bool _transferInProgress;

    private async Task<bool> PrepareTransferAsync()
    {
        if (_transferInProgress) return false;
        if (ViewModel.HasUnsavedChanges || !ViewModel.HasNamedProfile) return await SaveProfileAsync();
        return true;
    }
}
