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
        Check(!restored.ShowJingleDuration && restored.TitleFontSize == 15 && restored.FontFamily == "Consolas", "Profile appearance round trip excludes local title size");
        Check(JsonSerializer.Deserialize<AppSettings>("{\"titleFontSize\":23}")!.TitleFontSize == 23, "Legacy size is readable for migration");
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
        var titleOrigin = title.TransformToAncestor(button).Transform(new Point());
        var titleSize = title.RenderSize;
        jingle.Shortcut = "Ctrl+1";
        vm.Settings.ShowJingleDuration = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        mainContent.UpdateLayout();
        Check(title.TransformToAncestor(button).Transform(new Point()) == titleOrigin && title.RenderSize == titleSize,
            "Corner badges must not reserve title space");
        vm.Settings.DeckTabWidth = 160;
        vm.Settings.DeckTabHeight = 54;
        vm.Settings.DeckTabsPerRow = 2;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        mainContent.UpdateLayout();
        var tabsPanel = Descendants(mainContent).OfType<FloorballDJ.Controls.DeckTabsPanel>().Single();
        Check(tabsPanel.ActualHeight >= 108 && VisualTreeHelper.GetOffset(tabsPanel.Children[2]).Y >= 54,
            "Live multirow deck settings");
        vm.Decks[1].TabWidth = 220;
        vm.Decks[1].TabHeight = 70;
        vm.Decks[1].TabStartsNewRow = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        mainContent.UpdateLayout();
        Check(tabsPanel.Children[1].RenderSize.Width == 220 && tabsPanel.Children[1].RenderSize.Height >= 70 &&
            VisualTreeHelper.GetOffset(tabsPanel.Children[1]).Y >= 54, "Live per-deck sizing and row break");
        var mainImage = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        mainImage.Render(mainContent);
        var mainEncoder = new PngBitmapEncoder();
        mainEncoder.Frames.Add(BitmapFrame.Create(mainImage));
        using (var file = File.Create(Path.Combine(root, "main.png"))) mainEncoder.Save(file);
        Console.WriteLine("PASS: existing button updates, expanded title area, hidden duration.");
        Console.WriteLine(Path.Combine(root, "main.png"));
        var tabDialog = new DeckTabSettingsWindow(vm.Decks[1]);
        var tabContent = (FrameworkElement)tabDialog.Content;
        tabContent.Measure(new Size(480, 350));
        tabContent.Arrange(new Rect(0, 0, 480, 350));
        tabContent.UpdateLayout();
        var tabImage = new RenderTargetBitmap(480, 350, 96, 96, PixelFormats.Pbgra32);
        tabImage.Render(tabContent);
        var tabEncoder = new PngBitmapEncoder();
        tabEncoder.Frames.Add(BitmapFrame.Create(tabImage));
        using (var file = File.Create(Path.Combine(root, "tab-dialog.png"))) tabEncoder.Save(file);
        Console.WriteLine(Path.Combine(root, "tab-dialog.png"));
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
