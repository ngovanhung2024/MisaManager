using System.IO;
using System.Windows;
using System.Windows.Threading;
using MisaManager.Core;

namespace MisaManager.App;

public partial class App : Application
{
    private AppServices? services;
    private DateTimeOffset lastUnexpectedShown = DateTimeOffset.MinValue;
    public AppServices Services => services ?? throw new InvalidOperationException("Dịch vụ MisaManager chưa khởi động.");

    public App()
    {
        // An escaped exception must never kill the process mid-entry: log it and keep the UI alive.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrashLog("unobserved-task", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog("fatal", e.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { services = new AppServices(); }
        catch (Exception error)
        {
            var log = WriteCrashLog("startup", error);
            MessageBox.Show("MisaManager không khởi động được:\n\n" + error.Message
                + (log is null ? "" : "\n\nChi tiết: " + log), "MisaManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Drain the control/native owner before Program releases the lifetime mutex; bounded so exit never hangs.
        if (services is { } owned)
        {
            try { Task.Run(async () => await owned.DisposeAsync()).Wait(TimeSpan.FromSeconds(30)); }
            catch (Exception error) { WriteCrashLog("shutdown", error); }
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var log = WriteCrashLog("ui", e.Exception);
        services?.RecordUiError("Lỗi giao diện không mong muốn: " + e.Exception.Message, e.Exception.ToString());
        // Rate-limited: a failing timer tick must not stack dialogs.
        if (DateTimeOffset.UtcNow - lastUnexpectedShown < TimeSpan.FromSeconds(30)) return;
        lastUnexpectedShown = DateTimeOffset.UtcNow;
        if (MainWindow is MainWindow window) window.ReportUnexpectedError(e.Exception, log);
    }

    internal static string? WriteCrashLog(string kind, Exception? error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.TechnicalName, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {kind}: {error}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch { return null; }
    }
}
