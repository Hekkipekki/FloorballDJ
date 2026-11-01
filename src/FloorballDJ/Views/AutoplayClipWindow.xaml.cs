using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FloorballDJ.Services;

namespace FloorballDJ.Views;

public partial class AutoplayClipWindow : Window
{
    private readonly double _start;
    private readonly double _duration;
    public double? EndSeconds { get; private set; }
    public AutoplayClipWindow(string title, double start, double duration, double? customEnd)
    {
        _start = start; _duration = duration;
        InitializeComponent();
        SongTitle.Text = title;
        RangeText.Text = string.Format(LanguageService.Translate("Sluttiden räknas från filens början. Start: {0}. Filens längd: {1}."), Format(start), Format(duration));
        EndTimeBox.Text = customEnd.HasValue ? Format(customEnd.Value) : "";
        Validate();
        WindowPlacementService.FitToOwnerMonitor(this);
        Loaded += (_, _) => { EndTimeBox.Focus(); EndTimeBox.SelectAll(); };
    }
    internal static string Format(double seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.###}";
    internal static bool TryParse(string text, out double seconds)
    {
        var parts = text.Trim().Replace(',', '.').Split(':');
        seconds = 0;
        if (parts.Length is < 1 or > 3) return false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 || (i > 0 && value >= 60)) return false;
            seconds = seconds * 60 + value;
        }
        return double.IsFinite(seconds);
    }
    private bool Validate()
    {
        if (ApplyButton is null) return false;
        var empty = string.IsNullOrWhiteSpace(EndTimeBox.Text);
        var seconds = 0d;
        var valid = empty || (TryParse(EndTimeBox.Text, out seconds) && seconds > _start && seconds <= _duration);
        ApplyButton.IsEnabled = valid;
        ErrorText.Text = valid ? "" : LanguageService.Translate("Sluttiden måste vara efter start och inom filens längd.");
        if (valid) EndSeconds = empty ? null : seconds;
        return valid;
    }
    private void EndTime_Changed(object sender, TextChangedEventArgs e) => Validate();
    private void Apply_Click(object sender, RoutedEventArgs e) { if (Validate()) DialogResult = true; }
}
