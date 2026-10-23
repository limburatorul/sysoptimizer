using System.IO;
using System.Windows;
using Sysoptimizer.Services;

namespace Sysoptimizer;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    /// <summary>Where the installer put the app before 1.20; Program Files\ProtagonistLabs\Sysoptimizer since.</summary>
    private static readonly string LegacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sysoptimizer");

    protected override void OnStartup(StartupEventArgs e)
    {
        // The self-update of a version before 1.20 starts the exe where it was once the installer is done. For that one
        // start the installer leaves the new exe there; it hands over to the installed copy, which then clears the old folder.
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProtagonistLabs", "Sysoptimizer", "Sysoptimizer.exe");
        if (SameFolder(AppContext.BaseDirectory, LegacyFolder) && File.Exists(installed))
        {
            var start = new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = false };
            foreach (var arg in e.Args) start.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(start);
            Shutdown();
            return;
        }

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

        // Before anything opens a data file: everything moves from ProgramData to the studio's folder (1.20).
        string data = HistoryStore.MoveData(HistoryStore.OldDataFolder, HistoryStore.DataFolder, out string? moved);
        HistoryStore.Folder = Path.Combine(data, "history");
        ChangeService.Folder = data;
        AppLog.File = Path.Combine(data, "sysoptimizer.log");
        if (moved != null) AppLog.Write(moved);

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
            Sysoptimizer.MainWindow.RepointClaude();
            Thread.Sleep(TimeSpan.FromSeconds(10)); // the old copy that handed over may still be closing
            RemoveLegacyFolder();
            while (true)
            {
                ShareGames();
                Thread.Sleep(TimeSpan.FromHours(1));
            }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Ecosystem" }.Start();
        Exit += (_, _) => ShareGames();
    }

    private static bool SameFolder(string a, string b) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a version before 1.20 left in Program Files\Sysoptimizer when the installer couldn't remove it (a file in use,
    /// or the exe kept for the hand-over above). Only the installed copy does this, never a portable one, and only the
    /// installer's own files go: a folder with anything else in it stays.
    /// </summary>
    private static void RemoveLegacyFolder()
    {
        if (Updater.Portable || !Directory.Exists(LegacyFolder) || SameFolder(AppContext.BaseDirectory, LegacyFolder)) return;
        try
        {
            foreach (var name in new[] { "Sysoptimizer.exe", "Sysoptimizer.pdb", "sysoptimizer-mcp.exe", "unins000.exe", "unins000.dat" })
                File.Delete(Path.Combine(LegacyFolder, name));
            Directory.Delete(LegacyFolder);
            AppLog.Write($"Removed the old program folder {LegacyFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write($"Old program folder {LegacyFolder} not removed yet: {ex.Message}"); }
    }

    private static void ShareGames()
    {
        try { Ecosystem.WriteGames(); }
        catch (Exception ex) { AppLog.Write($"Couldn't share the games' sessions: {ex.Message}"); }
    }
}
