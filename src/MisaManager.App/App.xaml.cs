using System.Windows;

namespace MisaManager.App;

public partial class App : Application
{
    public AppServices Services { get; } = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // Drain the control/native owner before Program releases the lifetime mutex.
        Task.Run(async () => await Services.DisposeAsync()).GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
