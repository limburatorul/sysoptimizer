using System.Windows;

namespace Sysoptimizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.LoadSaved();
        base.OnStartup(e);
    }
}
