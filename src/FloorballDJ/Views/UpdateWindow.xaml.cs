using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates = new();
    private UpdateManifest? _manifest;
    private CancellationTokenSource? _downloadCancellation;

    public string? InstallerPath { get; private set; }

    public UpdateWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Installerad version: {_updates.CurrentVersion}";
        Loaded += async (_, _) => await CheckAsync();
        Closed += (_, _) => _downloadCancellation?.Cancel();
    }

    private async Task CheckAsync()
    {
        SetCheckingState();
        try
        {
            _manifest = await _updates.CheckAsync();
            ReleaseNotesButton.Visibility = Visibility.Visible;
            DownloadPageButton.Visibility = Visibility.Visible;
            if (_updates.IsNewer(_manifest))
            {
                StateIcon.Text = "↓";
                StateTitle.Text = "En ny version finns";
                VersionText.Text = $"Installerad: {_updates.CurrentVersion}  ·  Tillgänglig: {_manifest.Version}";
                SummaryText.Text = string.IsNullOrWhiteSpace(_manifest.Summary)
                    ? "Uppdateringen kan hämtas och installeras direkt."
                    : _manifest.Summary;
                InstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                StateIcon.Text = "✓";
                StateTitle.Text = "Du använder senaste versionen";
                VersionText.Text = $"Installerad version: {_updates.CurrentVersion}";
                SummaryText.Text = "Ingen nyare version finns på den valda betakanalen.";
            }
        }
        catch (Exception ex)
        {
            StateIcon.Text = "!";
            StateTitle.Text = "Kunde inte söka efter uppdateringar";
            VersionText.Text = $"Installerad version: {_updates.CurrentVersion}";
            SummaryText.Text = "Kontrollera internetanslutningen och försök igen. FloorballDJ påverkas inte av felet.";
            DownloadStatusText.Text = ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void SetCheckingState()
    {
        StateIcon.Text = "↻";
        StateTitle.Text = "Kontrollerar…";
        VersionText.Text = $"Installerad version: {_updates.CurrentVersion}";
        SummaryText.Text = "Hämtar den senaste versionsinformationen från floorballdj.netlify.app.";
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
        DownloadStatusText.Text = "Laddar ned installationsfilen…";
        _downloadCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value;
                DownloadStatusText.Text = $"Laddar ned… {value:0}%";
            });
            InstallerPath = await _updates.DownloadInstallerAsync(_manifest, progress, _downloadCancellation.Token);
            DownloadStatusText.Text = "Nedladdningen är verifierad och klar.";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "Nedladdningen avbröts.";
            InstallButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = ex.Message;
            InstallButton.IsEnabled = true;
            RetryButton.IsEnabled = true;
        }
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is not null) UpdateService.OpenWebPage(_manifest.ReleaseNotesUrl);
    }

    private void DownloadPage_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is not null) UpdateService.OpenWebPage(_manifest.DownloadPage);
    }
}
