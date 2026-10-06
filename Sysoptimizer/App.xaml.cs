using System.Windows;
using Sysoptimizer.Services;

namespace Sysoptimizer;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One instance only: a second recorder would write every sample into the history twice. A second
        // launch instead asks the running one (often hidden in the tray) to show itself, then exits.
        _singleInstance = new Mutex(true, @"Local\Sysoptimizer.SingleInstance", out bool first);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sysoptimizer.Show");
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }

        ThemeManager.LoadSaved();
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains(BackgroundMode.TrayArgument)) window.Show();

        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();
    }
}
