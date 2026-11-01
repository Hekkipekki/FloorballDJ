using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloorballDJ.Services;
using FloorballDJ.Views;

internal static class UpdateNotificationChecks
{
    private static UpdateManifest Manifest(string version) => new(version, "rc",
        "https://github.com/Hekkipekki/FloorballDJ/releases/download/v" + version + "/FloorballDJ-Setup.exe",
        "https://floorballdj.netlify.app/licens/#download", "https://floorballdj.netlify.app/changelog/",
        new string('a', 64), null, "Fixture release");

    internal static async Task RunAsync(string root, string? output)
    {
        var updates = new UpdateService();
        var current = updates.CurrentVersion;
        var version = Version.Parse(current.Split('-')[0]);
        var newer = Manifest($"{version.Major}.{version.Minor}.{version.Build + 1}");
        Check(!updates.IsNewer(Manifest(current)) && !updates.IsNewer(Manifest("0.0.1")) && updates.IsNewer(newer),
            "Only a strictly newer version is offered");
        var compare = typeof(UpdateService).GetNestedType("SemanticVersion", BindingFlags.NonPublic)!.GetMethod("Compare")!;
        Check((int)compare.Invoke(null, ["0.40.0-rc.10", "0.40.0-rc.9"])! > 0 &&
            (int)compare.Invoke(null, ["0.40.0", "0.40.0-rc.11"])! > 0, "Numeric RC ordering and stable release precedence");
        var locale = LanguageService.CurrentLanguage;
        try
        {
            foreach (var language in new[] { "sv", "en" })
            {
                LanguageService.SetLanguage(language);
                await WithMain(root, async main =>
                {
                    var pending = new TaskCompletionSource<UpdateManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var calls = 0;
                    main.Check = _ => { calls++; return pending.Task; };
                    var work = main.Window.CheckForStartupUpdatesAsync();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(!work.IsCompleted && main.Window.IsEnabled, "Network checking leaves the main window responsive");
                    var seen = false; Exception? failure = null;
                    var inspection = InspectPopupAsync(dialog =>
                    {
                        seen = true;
                        try
                        {
                            LanguageService.TranslateTree(dialog);
                            Check(((TextBlock)dialog.FindName("HeadingText")).Text == LanguageService.Translate("Utdaterad version"), "Localized outdated heading");
                            Check(((TextBlock)dialog.FindName("VersionText")).Text.Contains(current) && ((TextBlock)dialog.FindName("VersionText")).Text.Contains(newer.Version), "Installed and available versions are shown");
                            var later = (Button)dialog.FindName("CloseButton"); var install = (Button)dialog.FindName("InstallButton");
                            Check(later.IsDefault && later.Content as string == LanguageService.Translate("Senare") && install.IsVisible, "Later is the safe default and Update is offered");
                            Check(install.Content as string == (LanguageService.IsEnglish ? "Update" : "Uppdatera"), "Install action says Update rather than Refresh");
                            var laterBounds = later.TransformToAncestor(dialog).TransformBounds(new Rect(later.RenderSize));
                            var installBounds = install.TransformToAncestor(dialog).TransformBounds(new Rect(install.RenderSize));
                            Check(!laterBounds.IntersectsWith(installBounds) && installBounds.Right <= dialog.ActualWidth, "Action buttons fit without overlap");
                            var content = (FrameworkElement)dialog.Content;
                            Check(install.TransformToAncestor(content).TransformBounds(new Rect(install.RenderSize)).Bottom <= content.ActualHeight + 1,
                                "Update action remains fully visible inside the dialog content");
                            Check(dialog.InstallerPath is null, "Notification does not download or install without a choice");
                            if (output is not null) Save((FrameworkElement)dialog.Content, output, $"startup-update-{language}.png");
                        }
                        catch (Exception exception) { failure = exception; }
                        finally { ((Button)dialog.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    });
                    pending.SetResult(newer);
                    await Task.WhenAll(work, inspection).WaitAsync(TimeSpan.FromSeconds(5));
                    if (failure is not null) throw failure;
                    Check(seen && main.Window.IsEnabled, "Later returns to the usable main window");
                    await main.Window.CheckForStartupUpdatesAsync();
                    Check(calls == 1 && !Application.Current.Windows.OfType<UpdateWindow>().Any(), "Only one automatic check per startup");
                });
                await CheckHelp(output, language);
            }
            foreach (var response in new[] { Manifest(current), Manifest("0.0.1"), Manifest("invalid") })
                await WithMain(root, async main =>
                {
                    main.Check = _ => Task.FromResult(response);
                    await main.Window.CheckForStartupUpdatesAsync();
                    Check(!Application.Current.Windows.OfType<UpdateWindow>().Any(), "Current, older and invalid versions never notify");
                });
            await WithMain(root, async main =>
            {
                main.Check = _ => Task.FromException<UpdateManifest>(new HttpRequestException("offline fixture"));
                await main.Window.CheckForStartupUpdatesAsync();
                Check(main.Window.IsEnabled && !Application.Current.Windows.OfType<UpdateWindow>().Any(), "Offline startup is silent");
            });
            await WithMain(root, async main =>
            {
                var pending = new TaskCompletionSource<UpdateManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken observed = default;
                main.Check = token => { observed = token; return pending.Task; };
                await main.Window.CheckForStartupUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(observed.IsCancellationRequested, "Slow provider is cancelled even when it ignores cancellation");
                pending.SetResult(newer); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!Application.Current.Windows.OfType<UpdateWindow>().Any(), "Timed-out results cannot show a late popup");
            }, TimeSpan.FromMilliseconds(80));
            await WithMain(root, async main =>
            {
                var pending = new TaskCompletionSource<UpdateManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
                main.Check = _ => pending.Task;
                var work = main.Window.CheckForStartupUpdatesAsync();
                main.Window.Close(); await main.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pending.SetResult(newer); await work.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!Application.Current.Windows.OfType<UpdateWindow>().Any(), "Closing the main window cancels pending notification");
            });
        }
        finally { LanguageService.SetLanguage(locale); }
        Console.WriteLine("PASS: responsive once-per-startup update notification; semantic versions, Later, current/older/invalid/offline/timeout/close; Swedish/English popup and updated searchable Autoplay/update help.");
    }

    private static async Task InspectPopupAsync(Action<UpdateWindow> inspect)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        UpdateWindow? dialog;
        while ((dialog = Application.Current.Windows.OfType<UpdateWindow>().FirstOrDefault(window => window.IsLoaded)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Check(dialog is not null, "Expected update popup");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        inspect(dialog!);
    }

    private sealed class Fixture
    {
        internal MainWindow Window = null!;
        internal Func<CancellationToken, Task<UpdateManifest>> Check = _ => Task.FromException<UpdateManifest>(new InvalidOperationException("Fixture not set"));
        internal TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static async Task WithMain(string root, Func<Fixture, Task> action, TimeSpan? timeout = null)
    {
        var previousDirectory = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        var previousMain = Application.Current.MainWindow;
        var directory = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", directory);
        var fixture = new Fixture();
        try
        {
            fixture.Window = new MainWindow(new LicenseService(), false)
            {
                ShowInTaskbar = false, ConfirmUnsavedProfile = () => UnsavedProfileChoice.Discard,
                CheckUpdateAsync = token => fixture.Check(token), StartupUpdateTimeout = timeout ?? TimeSpan.FromSeconds(3)
            };
            fixture.Window.Closed += (_, _) => fixture.Closed.TrySetResult();
            Application.Current.MainWindow = fixture.Window;
            fixture.Window.Show();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!fixture.Window.StartupReady && DateTime.UtcNow < deadline) await Task.Delay(15);
            Check(fixture.Window.StartupReady, "Fixture main window is ready");
            await action(fixture);
        }
        finally
        {
            if (fixture.Window is not null && !fixture.Closed.Task.IsCompleted) { fixture.Window.Close(); await fixture.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            Application.Current.MainWindow = previousMain;
            Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", previousDirectory);
        }
    }

    private static async Task CheckHelp(string? output, string language)
    {
        var help = new HelpWindow { ShowInTaskbar = false };
        try
        {
            help.Show(); help.WindowState = WindowState.Normal; help.Width = 1366; help.Height = 850;
            LanguageService.TranslateTree(help);
            var show = typeof(HelpWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            show.Invoke(help, ["autoplay"]); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var page = (ScrollViewer)help.FindName("AutoplayPage");
            var text = string.Join(" ", Children(page).OfType<TextBlock>().Select(block => block.Text));
            Check(text.Contains(language == "en" ? "session indicator is hidden" : "gröna sessionsikonen är dold") &&
                text.Contains(language == "en" ? "two playlists" : "två spellistor") && text.Contains(language == "en" ? "Select page" : "Markera sidan") &&
                text.Contains(language == "en" ? "without a save prompt" : "utan sparfråga"), "Autoplay help covers two lists, bulk selection, save prompts and picker/session markers");
            if (output is not null) Save((FrameworkElement)help.Content, output, $"help-autoplay-{language}.png");
            show.Invoke(help, ["license"]); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var updateText = string.Join(" ", Children((ScrollViewer)help.FindName("LicensingPage")).OfType<TextBlock>().Select(block => block.Text));
            Check(updateText.Contains(language == "en" ? "At startup" : "När programmet startar") &&
                !updateText.Contains("Kontrollen körs endast"), "Help describes automatic startup checking and offline behavior");
            ((TextBox)help.FindName("HelpSearchBox")).Text = language == "en" ? "startup" : "Startkontrollen";
            Check(((ListBox)help.FindName("SearchResultsList")).Items.Count > 0, "Updated help is searchable");
            if (output is not null) Save((FrameworkElement)help.Content, output, $"help-updates-{language}.png");
        }
        finally { help.Close(); }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement element, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var offset = VisualTreeHelper.GetOffset(element);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)element.FindResource("AppBackgroundBrush"), null, new Rect(element.RenderSize));
            drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(new Point(offset.X, offset.Y), element.RenderSize) }, null, new Rect(element.RenderSize));
        }
        bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
