using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class ButtonAppearanceWindow : Window
{
    private static readonly Regex HexColorPattern = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    private readonly ColorPresetService _presetService = new();
    private readonly ObservableCollection<ColorPreset> _presets = [];
    private bool _editingButtonColor = true;
    private bool _draggingColor;
    private bool _updatingText;
    private bool _updatingPicker;
    private bool _syncingActiveHex;
    private double _hue;
    private double _saturation;
    private double _value;

    public string ButtonColor { get; private set; }
    public string TextColor { get; private set; }

    public ButtonAppearanceWindow(string buttonColor, string textColor, bool appliesToRow,
        string? fontFamily = null, double fontSize = 15)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        Title = appliesToRow ? "Hela radens färger" : "Knappens färger";
        ButtonColor = NormalizeHex(buttonColor, "#142238");
        TextColor = NormalizeHex(textColor, "#F7FAFC");

        _updatingText = true;
        ButtonColorBox.Text = ButtonColor;
        TextColorBox.Text = TextColor;
        _updatingText = false;

        PreviewText.FontFamily = FontService.Resolve(fontFamily);
        PreviewText.FontSize = Math.Clamp(fontSize, 9, 40);
        foreach (var preset in _presetService.Load().OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            _presets.Add(preset);

        Loaded += (_, _) => ActivateEditor(true);
        UpdatePreview();
    }

    private void SavedColors_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SavedColorsButton };
        if (_presets.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Inga sparade färger ännu", IsEnabled = false });
        else
            foreach (var preset in _presets)
            {
                var item = new MenuItem { Header = CreatePresetHeader(preset) };
                item.Click += (_, _) => ApplyPreset(preset);
                menu.Items.Add(item);
            }

        menu.Items.Add(new Separator());
        var saveItem = new MenuItem { Header = "Spara nuvarande kombination…" };
        saveItem.Click += (_, _) => SaveCurrentPreset();
        menu.Items.Add(saveItem);

        if (_presets.Count > 0)
        {
            var deleteMenu = new MenuItem { Header = "Ta bort sparad färg" };
            foreach (var preset in _presets)
            {
                var deleteItem = new MenuItem { Header = preset.Name };
                deleteItem.Click += (_, _) => DeletePreset(preset);
                deleteMenu.Items.Add(deleteItem);
            }
            menu.Items.Add(deleteMenu);
        }
        SavedColorsButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static FrameworkElement CreatePresetHeader(ColorPreset preset)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 5, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.ButtonColor)!)
        });
        panel.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.TextColor)!)
        });
        panel.Children.Add(new TextBlock { Text = preset.Name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void ApplyPreset(ColorPreset preset)
    {
        ButtonColorBox.Text = preset.ButtonColor;
        TextColorBox.Text = preset.TextColor;
        ActivateEditor(true);
    }

    private void SaveCurrentPreset()
    {
        if (!TryColor(ButtonColorBox.Text, out _) || !TryColor(TextColorBox.Text, out _))
        {
            MessageBox.Show(this, "Båda färgerna måste ha giltiga HEX-värden innan kombinationen kan sparas.",
                "Kontrollera färgerna", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = new TextPromptWindow("Spara färgkombination", "Namnge färgkombinationen",
            "Namnet hjälper dig att hitta kombinationen nästa gång.") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var existing = _presets.FirstOrDefault(item => string.Equals(item.Name, prompt.Value, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) _presets.Remove(existing);
        _presets.Add(new ColorPreset(prompt.Value, ButtonColorBox.Text.Trim().ToUpperInvariant(), TextColorBox.Text.Trim().ToUpperInvariant()));
        SortAndSavePresets();
    }

    private void DeletePreset(ColorPreset preset)
    {
        if (MessageBox.Show(this, $"Ta bort färgkombinationen '{preset.Name}'?", "Ta bort färgkombination",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _presets.Remove(preset);
        _presetService.Save(_presets);
    }

    private void SortAndSavePresets()
    {
        var sorted = _presets.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _presets.Clear();
        foreach (var item in sorted) _presets.Add(item);
        _presetService.Save(_presets);
    }

    private void SetButtonColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ButtonColorBox.Text = color;
            ActivateEditor(true);
        }
    }

    private void SetTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            TextColorBox.Text = color;
            ActivateEditor(false);
        }
    }

    private void SetQuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        if (_editingButtonColor) ButtonColorBox.Text = color;
        else TextColorBox.Text = color;
        ActivateEditor(_editingButtonColor);
    }

    private void EditButtonColor_Click(object sender, RoutedEventArgs e) => ActivateEditor(true);
    private void EditTextColor_Click(object sender, RoutedEventArgs e) => ActivateEditor(false);

    private void ActivateEditor(bool buttonColor)
    {
        _editingButtonColor = buttonColor;
        EditButtonColorButton.Background = buttonColor ? FindResource("AccentBrush") as Brush : FindResource("PanelBrush") as Brush;
        EditButtonColorButton.Foreground = buttonColor ? Brushes.Black : FindResource("TextBrush") as Brush;
        EditTextColorButton.Background = !buttonColor ? FindResource("AccentBrush") as Brush : FindResource("PanelBrush") as Brush;
        EditTextColorButton.Foreground = !buttonColor ? Brushes.Black : FindResource("TextBrush") as Brush;
        QuickColorLabel.Text = buttonColor ? "SNABBVAL KNAPPFÄRG" : "SNABBVAL TEXTFÄRG";
        SyncActiveHexFromModel();

        var box = buttonColor ? ButtonColorBox : TextColorBox;
        if (TryColor(box.Text, out var color)) SetPickerFromColor(color);
    }

    private void ActiveHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingActiveHex || ButtonColorBox is null) return;
        var box = _editingButtonColor ? ButtonColorBox : TextColorBox;
        box.Text = ActiveHexBox.Text;
        UpdateActiveHexStatus();
        if (TryColor(box.Text, out var color)) SetPickerFromColor(color);
    }

    private void ColorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ButtonColorBox is null || TextColorBox is null) return;
        UpdatePreview();
        if (sender is TextBox box && ((_editingButtonColor && box == ButtonColorBox) || (!_editingButtonColor && box == TextColorBox)))
        {
            SyncActiveHexFromModel();
            if (!_updatingText && TryColor(box.Text, out var color)) SetPickerFromColor(color);
        }
    }

    private void SyncActiveHexFromModel()
    {
        if (ActiveHexBox is null) return;
        _syncingActiveHex = true;
        ActiveHexBox.Text = (_editingButtonColor ? ButtonColorBox : TextColorBox).Text;
        ActiveHexBox.CaretIndex = ActiveHexBox.Text.Length;
        _syncingActiveHex = false;
        UpdateActiveHexStatus();
    }

    private void UpdateActiveHexStatus()
    {
        if (ActiveHexStatus is null) return;
        var valid = TryColor(ActiveHexBox.Text, out _);
        ActiveHexStatus.Text = valid ? (_editingButtonColor ? "Knappfärg" : "Textfärg") : "Ogiltigt HEX-värde";
        ActiveHexStatus.Foreground = new SolidColorBrush(valid ? Color.FromRgb(54, 224, 180) : Color.FromRgb(255, 142, 160));
        ActiveHexBox.BorderBrush = new SolidColorBrush(valid ? Color.FromRgb(67, 88, 119) : Color.FromRgb(255, 142, 160));
    }

    private void UpdatePreview()
    {
        if (PreviewBorder is null || PreviewText is null) return;
        if (TryColor(ButtonColorBox.Text, out var background)) PreviewBorder.Background = new SolidColorBrush(background);
        if (TryColor(TextColorBox.Text, out var foreground)) PreviewText.Foreground = new SolidColorBrush(foreground);
    }

    private void SetPickerFromColor(Color color)
    {
        RgbToHsv(color, out _hue, out _saturation, out _value);
        _updatingPicker = true;
        HueSlider.Value = _hue;
        _updatingPicker = false;
        RefreshPickerVisuals(color);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPicker || ColorFieldBase is null) return;
        _hue = e.NewValue;
        ApplyPickerColor();
    }

    private void ColorField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingColor = true;
        ColorFieldInput.CaptureMouse();
        SetColorFromPoint(e.GetPosition(ColorFieldInput));
    }

    private void ColorField_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingColor && e.LeftButton == MouseButtonState.Pressed) SetColorFromPoint(e.GetPosition(ColorFieldInput));
    }

    private void ColorField_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingColor) return;
        SetColorFromPoint(e.GetPosition(ColorFieldInput));
        _draggingColor = false;
        ColorFieldInput.ReleaseMouseCapture();
    }

    private void SetColorFromPoint(Point point)
    {
        if (ColorFieldInput.ActualWidth <= 0 || ColorFieldInput.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / ColorFieldInput.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / ColorFieldInput.ActualHeight, 0, 1);
        ApplyPickerColor();
    }

    private void ApplyPickerColor()
    {
        var color = HsvToColor(_hue, _saturation, _value);
        var box = _editingButtonColor ? ButtonColorBox : TextColorBox;
        _updatingText = true;
        box.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _updatingText = false;
        SyncActiveHexFromModel();
        UpdatePreview();
        RefreshPickerVisuals(color);
    }

    private void RefreshPickerVisuals(Color color)
    {
        ColorFieldBase.Background = new SolidColorBrush(HsvToColor(_hue, 1, 1));
        RgbValueText.Text = $"{color.R}, {color.G}, {color.B}";
        HsvValueText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{Math.Round(_hue)}°, {Math.Round(_saturation * 100)}%, {Math.Round(_value * 100)}%");
        var width = Math.Max(0, ColorFieldInput.ActualWidth);
        var height = Math.Max(0, ColorFieldInput.ActualHeight);
        Canvas.SetLeft(ColorCursor, (_saturation * width) - (ColorCursor.Width / 2));
        Canvas.SetTop(ColorCursor, ((1 - _value) * height) - (ColorCursor.Height / 2));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryColor(ButtonColorBox.Text, out _) || !TryColor(TextColorBox.Text, out _))
        {
            MessageBox.Show(this, "Ange giltiga HEX-färger i formatet #RRGGBB, exempelvis #142238 eller #F7FAFC.",
                "Kontrollera färgerna", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ButtonColor = ButtonColorBox.Text.Trim().ToUpperInvariant();
        TextColor = TextColorBox.Text.Trim().ToUpperInvariant();
        DialogResult = true;
    }

    private static string NormalizeHex(string? value, string fallback) => TryColor(value, out _) ? value!.Trim().ToUpperInvariant() : fallback;

    private static bool TryColor(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim();
        if (text is null || !HexColorPattern.IsMatch(text)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(text)!; return true; }
        catch { return false; }
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
