using System.Windows;
using System.Windows.Media;
using FloorballDJ.Models;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class LicenseWindow : Window
{
    private readonly LicenseService _licenses;
    private readonly bool _isStartup;
    private LicenseEvaluation _evaluation;

    public bool LicenseWasDeactivated { get; private set; }

    public LicenseWindow(LicenseService licenses, LicenseEvaluation evaluation, bool isStartup)
    {
        InitializeComponent();
        _licenses = licenses;
        _evaluation = evaluation;
        _isStartup = isStartup;
        if (!isStartup && Application.Current.MainWindow is { } owner)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        UpdateStatus();
        Loaded += (_, _) => LicenseKeyBox.Focus();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _evaluation = await _licenses.EvaluateAsync();
            UpdateStatus();
            if (_isStartup && _evaluation.IsAllowed) DialogResult = true;
        });
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _evaluation = await _licenses.ActivateAsync(LicenseKeyBox.Text);
            UpdateStatus();
            if (_evaluation.IsAllowed)
            {
                LicenseKeyBox.Clear();
                if (_isStartup) DialogResult = true;
            }
        });
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Vill du avaktivera licensen på den här datorn? Internet krävs och FloorballDJ stängs efteråt.",
                "Avaktivera licens", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunBusyAsync(async () =>
        {
            var result = await _licenses.DeactivateAsync();
            if (!result.Success)
            {
                StatusMessage.Text = result.Message;
                return;
            }
            LicenseWasDeactivated = true;
            DialogResult = true;
        });
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void UpdateStatus()
    {
        StatusTitle.Text = _evaluation.Kind switch
        {
            LicenseAccessKind.Trial => "Provperiod aktiv",
            LicenseAccessKind.Licensed => "Licens aktiv",
            LicenseAccessKind.Expired => "Provperioden har gått ut",
            LicenseAccessKind.InternetRequired => "Internetanslutning krävs",
            LicenseAccessKind.Invalid => "Licensen kunde inte aktiveras",
            _ => "Ingen aktiv licens"
        };
        StatusMessage.Text = _evaluation.Message;
        StatusDot.Fill = (Brush)FindResource(_evaluation.IsAllowed ? "AccentBrush" :
            _evaluation.Kind == LicenseAccessKind.Invalid ? "DangerBrush" : "WarningBrush");
        ContinueButton.Visibility = !_isStartup && _evaluation.IsAllowed ? Visibility.Visible : Visibility.Collapsed;
        DeactivateButton.Visibility = _evaluation.Kind == LicenseAccessKind.Licensed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        RetryButton.IsEnabled = false;
        ActivateButton.IsEnabled = false;
        DeactivateButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            StatusTitle.Text = "Licenskontrollen misslyckades";
            StatusMessage.Text = ex.Message;
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            RetryButton.IsEnabled = true;
            ActivateButton.IsEnabled = true;
            DeactivateButton.IsEnabled = true;
        }
    }
}
