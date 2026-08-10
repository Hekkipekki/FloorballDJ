using System.Windows;
using System.Windows.Controls;

namespace FloorballDJ.Views;

public partial class HelpWindow : Window
{
    private ScrollViewer[] _pages = [];

    public HelpWindow()
    {
        InitializeComponent();
        _pages = [QuickStartPage, PlaybackPage, DecksPage, AutoplayPage, AudioPage,
            PropertiesPage, ProfilesPage, ShortcutsPage, LicensingPage, TroubleshootingPage];
        HelpNavigation.SelectedIndex = 0;
    }

    private void HelpNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0) return;
        var selected = Math.Clamp(HelpNavigation.SelectedIndex, 0, _pages.Length - 1);
        for (var index = 0; index < _pages.Length; index++)
        {
            _pages[index].Visibility = index == selected ? Visibility.Visible : Visibility.Collapsed;
            if (index == selected) _pages[index].ScrollToTop();
        }
    }
}
