using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FloorballDJ.Services;
using FloorballDJ.Views;
using FloorballDJ.Models;

namespace FloorballDJ;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(TextBox), Keyboard.KeyDownEvent,
            new KeyEventHandler(TextBox_KeyDown), true);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        var licensing = new LicenseService();
#if DEBUG
        var bypassForSmokeTest = Environment.GetCommandLineArgs()
            .Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
#else
        const bool bypassForSmokeTest = false;
#endif
        var access = bypassForSmokeTest
            ? new LicenseEvaluation(LicenseAccessKind.Licensed, true, "Intern visuell kontroll.")
            : await licensing.EvaluateAsync();
        if (!access.IsAllowed)
        {
            var activation = new LicenseWindow(licensing, access, isStartup: true);
            if (activation.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow(licensing);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    private static void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { AcceptsReturn: false } textBox || textBox.Name is "StartText" or "EndText") return;
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.log"), e.Exception.ToString()); } catch { }
            e.Handled = true;
            Shutdown(-1);
            return;
        }
        var path = WriteCrashLog(e.Exception);
        MessageBox.Show($"FloorballDJ stötte på ett oväntat fel.\n\nEn teknisk logg har sparats här:\n{path}",
            "FloorballDJ – fel", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    private static string WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloorballDJ", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"FloorballDJ crash report\nTime: {DateTimeOffset.Now:O}\nVersion: {typeof(App).Assembly.GetName().Version}\nOS: {Environment.OSVersion}\n.NET: {Environment.Version}\n\n{exception}");
            foreach (var oldLog in Directory.EnumerateFiles(directory, "crash-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(50))
                try { File.Delete(oldLog); } catch { }
            return path;
        }
        catch { return "Loggen kunde inte skrivas."; }
    }
}
