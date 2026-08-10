using System.Windows;
using System.Windows.Input;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class ShortcutCaptureWindow : Window
{
    public string? SelectedShortcut { get; private set; }

    public ShortcutCaptureWindow(string? currentShortcut)
    {
        InitializeComponent();
        SelectedShortcut = ShortcutService.Normalize(currentShortcut);
        PressedText.Text = SelectedShortcut ?? "Väntar på tangent …";
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    private void Capture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            DialogResult = false;
            return;
        }

        if (ShortcutService.IsModifierKey(key))
        {
            PressedText.Text = ShortcutService.ModifierPrompt();
            return;
        }

        var shortcut = ShortcutService.FromKeyEvent(e);
        if (shortcut is null) return;
        if (key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            PressedText.Text = "Space används redan för uppspelningskön";
            return;
        }

        SelectedShortcut = shortcut;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedShortcut = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
