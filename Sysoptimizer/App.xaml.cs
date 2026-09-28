using System.Windows;

namespace Sysoptimizer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.LoadSaved();
        base.OnStartup(e);
    }
}
