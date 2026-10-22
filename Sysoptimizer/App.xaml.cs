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

        // High, like Task Manager and for the same reason: a monitor has to keep running while something else
        // saturates every core — at Normal, a load running at High (a stress test, some games) starved the
        // whole app and the history stayed empty until it stopped. Its own work is light.
        System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;

        // Written down, not handled: what broke and where, for this PC's log file and a watching PC.
        AppLog.Write($"Sysoptimizer {Updater.Current} started");
        DispatcherUnhandledException += (_, args) => AppLog.Write($"Crash: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write($"Crash: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Write($"Error in background work: {args.Exception}");
        Exit += (_, args) => AppLog.Write($"Sysoptimizer stopped (exit code {args.ApplicationExitCode})");

        ThemeManager.LoadSaved();
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains(BackgroundMode.TrayArgument)) window.Show();
        WhatsNewWindow.OpenOnceAfterUpdate();

        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();

        // The Protagonist Labs shared folder: our card now, Shelf's games' sessions now and every hour, and once
        // more on the way out. Off the UI thread and at the lowest priority — the app itself runs at High.
        new Thread(() =>
        {
            try { Ecosystem.WriteCard(Environment.ProcessPath ?? "", Updater.Current); }
            catch (Exception ex) { AppLog.Write($"Couldn't write the app card: {ex.Message}"); }
            while (true)
            {
                ShareGames();
                Thread.Sleep(TimeSpan.FromHours(1));
            }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Ecosystem" }.Start();
        Exit += (_, _) => ShareGames();
    }

    private static void ShareGames()
    {
        try { Ecosystem.WriteGames(); }
        catch (Exception ex) { AppLog.Write($"Couldn't share the games' sessions: {ex.Message}"); }
    }
}
