using System.Globalization;
using System.Windows;

namespace YerelDisk.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
        base.OnStartup(e);
        // Developer fixtures live in a separate test runner, never in this EXE.
        if (e.Args.Length > 0) { Shutdown(2); return; }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
