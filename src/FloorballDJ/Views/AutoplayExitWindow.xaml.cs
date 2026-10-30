using System.Windows;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class AutoplayExitWindow : Window
{
    public AutoplayExitWindow()
    {
        InitializeComponent();
        WindowPlacementService.FitToOwnerMonitor(this);
        Loaded += (_, _) => StayButton.Focus();
    }

    private void Leave_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Stay_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
