using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.Views;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "FloorballDJ-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", root);
        var app = new App();
        app.InitializeComponent();
        var project = ProjectService.CreateDefault();
        var changes = new List<string?>();
        project.Settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        project.Settings.TitleFontSize = 23;
        project.Settings.FontFamily = "Consolas";
        project.Settings.ShowJingleDuration = false;
        Check(changes.SequenceEqual(new[] { "TitleFontSize", "FontFamily", "ShowJingleDuration" }), "Appearance notifications");
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(project.Settings))!;
        Check(!restored.ShowJingleDuration && restored.TitleFontSize == 23 && restored.FontFamily == "Consolas", "Round trip");
        Check(JsonSerializer.Deserialize<AppSettings>("{}")!.ShowJingleDuration, "Old profile default");
        var window = new SettingsWindow(project, [], new ProfilePreferencesService(root, Path.Combine(root, "autosave.json")));
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1000, 1800));
        content.Arrange(new Rect(0, 0, 1000, 1800));
        content.UpdateLayout();
        var preview = (TextBlock)window.FindName("TypographyPreview");
        Check(preview.FontSize == 23, "Initial preview");
        window.ViewData.Draft.TitleFontSize = 28;
        window.ViewData.Draft.FontFamily = "Arial";
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Check(preview.FontSize == 28 && preview.FontFamily.Source == "Arial", "Immediate preview");
        Check(project.Settings.TitleFontSize == 23, "Draft does not change saved settings");
        var combo = (ComboBox)window.FindName("RecentProfilesCombo");
        combo.ApplyTemplate();
        var chrome = (Border)combo.Template.FindName("Chrome", combo);
        Check(chrome.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(0x15, 0x21, 0x35), "Dark profile dropdown");
        content.UpdateLayout();
        var image = new RenderTargetBitmap(1000, 1800, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(root, "settings.png"))) encoder.Save(file);
        Console.WriteLine("PASS: notifications, serialization, legacy default, live preview, draft isolation, dark profile dropdown.");
        Console.WriteLine(Path.Combine(root, "settings.png"));
        var main = new MainWindow();
        var vm = (FloorballDJ.ViewModels.MainViewModel)main.DataContext;
        var jingle = vm.Decks[0].Jingles[0];
        jingle.Title = "En lång jingletitel med många ord som ska få plats på fler än två rader";
        jingle.FilePath = Path.Combine(root, "test.wav");
        jingle.DurationSeconds = 169;
        var mainContent = (FrameworkElement)main.Content;
        mainContent.Measure(new Size(1280, 720));
        mainContent.Arrange(new Rect(0, 0, 1280, 720));
        mainContent.UpdateLayout();
        var button = Descendants(mainContent).OfType<Button>().First(b => ReferenceEquals(b.DataContext, jingle));
        vm.Settings.TitleFontSize = 18;
        vm.Settings.FontFamily = "Consolas";
        vm.Settings.ShowJingleDuration = false;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        mainContent.UpdateLayout();
        Check(button.FontSize == 18 && button.FontFamily.Source == "Consolas", "Visible button updates without switching views");
        var title = Descendants(button).OfType<TextBlock>().First(t => t.Text == jingle.Title);
        Check(double.IsPositiveInfinity(title.MaxHeight) && title.ActualWidth > button.ActualWidth * .85, "Expanded title area");
        var duration = Descendants(button).OfType<TextBlock>().First(t => t.Text == "02:49");
        Check(((UIElement)VisualTreeHelper.GetParent(duration)).Visibility == Visibility.Collapsed, "Hidden duration");
        var mainImage = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        mainImage.Render(mainContent);
        var mainEncoder = new PngBitmapEncoder();
        mainEncoder.Frames.Add(BitmapFrame.Create(mainImage));
        using (var file = File.Create(Path.Combine(root, "main.png"))) mainEncoder.Save(file);
        Console.WriteLine("PASS: existing button updates, expanded title area, hidden duration.");
        Console.WriteLine(Path.Combine(root, "main.png"));
        vm.Dispose();
        app.Shutdown();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
