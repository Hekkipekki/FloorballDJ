using System.ComponentModel;
using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class BackupProgressWindow : Window, IProgress<PortableBackupProgress>
{
    private bool _allowClose;

    public BackupProgressWindow(string title, string heading, string description)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        WindowPlacementService.FitToOwnerMonitor(this);
        Loaded += (_, _) => { if (Owner is not null) Owner.IsEnabled = false; };
        Closing += BackupProgressWindow_Closing;
        Closed += (_, _) =>
        {
            if (Owner is null) return;
            Owner.IsEnabled = true;
            Owner.Activate();
        };
    }

    public void Report(PortableBackupProgress value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(value));
            return;
        }
        var percent = Math.Clamp(value.Percent, 0, 100);
        WorkProgress.Value = percent;
        PercentText.Text = $"{percent:0} %";
        PhaseText.Text = LanguageService.Translate(value.Phase);
        DetailText.Text = value.Detail ?? "";
        var fileLabel = LanguageService.IsEnglish ? "files" : "filer";
        FileCountText.Text = value.TotalFiles > 0 ? $"{value.CompletedFiles} / {value.TotalFiles} {fileLabel}" : "";
    }

    public void CloseAfterOperation()
    {
        _allowClose = true;
        Close();
    }

    private void BackupProgressWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
    }
}
