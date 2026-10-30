using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class MainWindow
{
    private bool _startupUpdateCheckStarted;
    private bool _updateDialogOpen;
    internal Func<CancellationToken, Task<UpdateManifest>> CheckUpdateAsync { get; init; } = new UpdateService().CheckAsync;
    internal TimeSpan StartupUpdateTimeout { get; init; } = TimeSpan.FromSeconds(8);

    internal async Task CheckForStartupUpdatesAsync()
    {
        if (_startupUpdateCheckStarted || _closeInProgress || _closeCommitted || !IsLoaded) return;
        _startupUpdateCheckStarted = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_backgroundWorkCancellation.Token);
        cancellation.CancelAfter(StartupUpdateTimeout);
        UpdateManifest manifest;
        try
        {
            // Optional network work must never delay startup or leave a late popup
            // after the window has closed, even if a provider ignores cancellation.
            manifest = await CheckUpdateAsync(cancellation.Token).WaitAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || !new UpdateService().IsNewer(manifest)) return;
        }
        catch (Exception)
        {
            // Offline, timeout and invalid remote data are silent at startup.
            // The manual Help command still reports errors and offers Retry.
            return;
        }
        if (_closeInProgress || _closeCommitted || !IsLoaded || !IsVisible || !IsEnabled || _updateDialogOpen) return;
        await ShowUpdateAndInstallAsync(manifest, startupNotification: true);
    }
}
