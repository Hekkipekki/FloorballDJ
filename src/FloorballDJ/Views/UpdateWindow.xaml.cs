using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates = new();
    private UpdateManifest? _manifest;
    private CancellationTokenSource? _downloadCancellation;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _startupNotification;
    private bool _closed;

    public string? InstallerPath { get; private set; }

    public UpdateWindow(UpdateManifest? manifest = null, bool startupNotification = false)
    {
        InitializeComponent();
        _manifest = manifest;
        _startupNotification = startupNotification;
        VersionText.Text = string.Format(LanguageService.Translate("Installerad version: {0}"), _updates.CurrentVersion);
        if (startupNotification)
        {
            HeadingText.Text = LanguageService.Translate("Utdaterad version");
            IntroText.Text = LanguageService.Translate("En nyare version av FloorballDJ finns. Uppdatera nu eller fortsätt med din nuvarande version.");
            CloseButton.Content = LanguageService.Translate("Senare");
            InstallButton.Content = LanguageService.IsEnglish ? "Update" : "Uppdatera";
        }
        WindowPlacementService.FitToOwnerMonitor(this);
        Loaded += async (_, _) =>
        {
            if (_manifest is not null) ShowManifest(_manifest);
            else await CheckAsync();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _downloadCancellation?.Cancel(); };
    }

    private async Task CheckAsync()
    {
        SetCheckingState();
        try
        {
            var manifest = await _updates.CheckAsync(_lifetime.Token);
            if (!_closed) ShowManifest(manifest);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (_closed) return;
            StateIcon.Text = "!";
            StateTitle.Text = LanguageService.Translate("Kunde inte söka efter uppdateringar");
            VersionText.Text = string.Format(LanguageService.Translate("Installerad version: {0}"), _updates.CurrentVersion);
            SummaryText.Text = LanguageService.Translate("Kontrollera internetanslutningen och försök igen. FloorballDJ påverkas inte av felet.");
            DownloadStatusText.Text = ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowManifest(UpdateManifest manifest)
    {
        _manifest = manifest;
        ReleaseNotesButton.Visibility = Visibility.Visible;
        DownloadPageButton.Visibility = Visibility.Visible;
        if (_updates.IsNewer(manifest))
        {
            StateIcon.Text = "↓";
            StateTitle.Text = LanguageService.Translate("En nyare version finns");
            VersionText.Text = string.Format(LanguageService.Translate("Installerad: {0}  ·  Tillgänglig: {1}"), _updates.CurrentVersion, manifest.Version);
            SummaryText.Text = _startupNotification
                ? LanguageService.Translate("Du använder en äldre version. Välj Uppdatera för att ladda ned och installera den nya versionen, eller Senare för att fortsätta som vanligt.")
                : string.IsNullOrWhiteSpace(manifest.Summary)
                    ? LanguageService.Translate("Uppdateringen kan hämtas och installeras direkt.") : manifest.Summary;
            InstallButton.Visibility = Visibility.Visible;
        }
        else
        {
            StateIcon.Text = "✓";
            StateTitle.Text = LanguageService.Translate("Du använder senaste versionen");
            VersionText.Text = string.Format(LanguageService.Translate("Installerad version: {0}"), _updates.CurrentVersion);
            SummaryText.Text = LanguageService.Translate("Ingen nyare publicerad version finns tillgänglig.");
            InstallButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCheckingState()
    {
        StateIcon.Text = "↻";
        StateTitle.Text = LanguageService.Translate("Kontrollerar…");
        VersionText.Text = string.Format(LanguageService.Translate("Installerad version: {0}"), _updates.CurrentVersion);
        SummaryText.Text = LanguageService.Translate("Hämtar den senaste versionsinformationen från floorballdj.netlify.app.");
        DownloadStatusText.Text = "";
        DownloadProgress.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ReleaseNotesButton.Visibility = Visibility.Collapsed;
        DownloadPageButton.Visibility = Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        InstallButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadStatusText.Text = LanguageService.Translate("Laddar ned installationsfilen…");
        _downloadCancellation?.Dispose();
        _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (_closed) return;
                DownloadProgress.Value = value;
                DownloadStatusText.Text = string.Format(LanguageService.Translate("Laddar ned… {0:0}%"), value);
            });
            InstallerPath = await _updates.DownloadInstallerAsync(_manifest, progress, _downloadCancellation.Token);
            if (_closed) return;
            DownloadStatusText.Text = LanguageService.Translate("Nedladdningen är verifierad och klar.");
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (_closed) return;
            DownloadStatusText.Text = LanguageService.Translate("Nedladdningen avbröts.");
            InstallButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            DownloadStatusText.Text = ex.Message;
            InstallButton.IsEnabled = true;
            RetryButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is not null) UpdateService.OpenWebPage(_manifest.ReleaseNotesUrl);
    }

    private void DownloadPage_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is not null) UpdateService.OpenWebPage(_manifest.DownloadPage);
    }
}
