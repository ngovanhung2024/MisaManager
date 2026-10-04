using System.Windows;

namespace MisaManager.FakeMisa;

/// <summary>
/// Usage: MISA.SME.exe [--profile path] [--scenario scenario.json] [--state state.json]
/// The state file receives every saved voucher so tests can assert exactly what MISA stored.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var profile = FakeProfile.Load(Option("--profile") ?? Path.Combine(AppContext.BaseDirectory, "profiles", "misa-sme2023-r20.json"));
        var scenario = Scenario.Load(Option("--scenario"));
        FakeLog.Open(Option("--state"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => FakeLog.Write("CRASH " + e.ExceptionObject);
        var store = new FakeStore(scenario, Option("--state"));
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var main = new MainWindow(profile, scenario, store);
        if (scenario.ShowLoginWindow)
            new Window { Title = "MISA SME 2023", Width = 420, Height = 260, Left = 40, Top = 40, Content = "Mở dữ liệu kế toán" }.Show();
        return app.Run(main);
    }
}
