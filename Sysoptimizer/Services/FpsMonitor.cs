using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;

namespace Sysoptimizer.Services;

/// <summary>
/// Frames per second of the games being played, the way PresentMon measures it: an ETW session sees each
/// present — the moment a finished frame is handed to Windows. DXGI's and Direct3D 9's are counted per swap
/// chain (an app's rate is its busiest one's, never several added up), and DXGI presents that only test whether
/// the window is visible are skipped; the graphics kernel's own Present event stands in for the apps that show
/// neither (some OpenGL and Vulkan ones). Needs admin.
///
/// 1.19: the rate comes from the frames' own timestamps, not from when the events arrive — ETW hands them over in
/// batches about a second apart, which made a steady 60 read 54, 66, 60…; and only games get one (see
/// <see cref="IsGame"/>), and only while in front: before, every app that drew had a rate, a dock at 1000 FPS and a mouse utility's hidden
/// web view at 60 among them.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "Sysoptimizer-FPS";
    private TraceEventSession? _session;
    private readonly object _gate = new();

    private sealed class Chain
    {
        public int Frames;           // since the last read
        public DateTime First, Last; // this read's first and last frame
        public DateTime Before;      // the last frame of the read before
        public DateTime ReadAt;
        public double Fps;
    }

    // Source 0 is the graphics kernel's (a stand-in), 1 DXGI's, 2 Direct3D 9's; Id is the swap chain's address.
    private readonly Dictionary<(int Pid, int Source, ulong Id), Chain> _chains = new();

    public string? Error { get; private set; }

    public FpsMonitor()
    {
        var thread = new Thread(Listen) { IsBackground = true, Name = "FPS (ETW)" };
        thread.Start();
    }

    private void Listen()
    {
        try
        {
            // A session left running by a crash keeps its name; opening it by name takes it over.
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider("Microsoft-Windows-DxgKrnl", TraceEventLevel.Informational, 0x1);
            _session.EnableProvider("Microsoft-Windows-DXGI", TraceEventLevel.Informational);
            _session.EnableProvider("Microsoft-Windows-D3D9", TraceEventLevel.Informational);
            _session.Source.Dynamic.All += OnEvent;
            _session.Source.Process(); // until disposed
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            Error = ex.Message;
        }
    }

    private const ulong DXGI_PRESENT_TEST = 0x1; // "would it be shown?" — no frame

    private void OnEvent(TraceEvent e)
    {
        if (e.ProcessID <= 0) return;
        int source;
        ulong id = 0;
        switch (e.ProviderName)
        {
            case "Microsoft-Windows-DxgKrnl" when e.EventName == "Present":
                source = 0;
                break;
            case "Microsoft-Windows-DXGI" when e.Opcode == TraceEventOpcode.Start && e.EventName.StartsWith("Present"):
                if ((Number(e, "Flags") & DXGI_PRESENT_TEST) != 0) return;
                (source, id) = (1, Number(e, "pIDXGISwapChain"));
                break;
            case "Microsoft-Windows-D3D9" when e.Opcode == TraceEventOpcode.Start && e.EventName.StartsWith("Present"):
                (source, id) = (2, Number(e, "pSwapchain"));
                break;
            default:
                return;
        }
        var at = e.TimeStamp;
        lock (_gate)
        {
            if (!_chains.TryGetValue((e.ProcessID, source, id), out var c)) _chains[(e.ProcessID, source, id)] = c = new Chain();
            if (c.Frames++ == 0) c.First = at;
            c.Last = at;
        }
    }

    private static ulong Number(TraceEvent e, string field)
    {
        try { return e.PayloadByName(field) is { } v ? Convert.ToUInt64(v) : 0; }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException) { return 0; }
    }

    /// <summary>
    /// Frames per second of the game in front, when it drew since the last call (empty otherwise): <paramref name="front"/>
    /// and its window are the app in front.
    /// </summary>
    public Dictionary<int, double> Read(int front, IntPtr frontWindow)
    {
        var now = DateTime.Now;
        var rates = new Dictionary<(int Pid, int Source), double>();
        lock (_gate)
        {
            foreach (var (key, c) in _chains.ToList())
            {
                if (c.Frames > 0)
                {
                    // The frames from the last one before to this read's last, by their own timestamps; with none
                    // close before (the first read, or after a pause), from this read's first.
                    bool joined = c.Before != default && (c.First - c.Before).TotalSeconds < 2;
                    double span = (c.Last - (joined ? c.Before : c.First)).TotalSeconds;
                    int frames = joined ? c.Frames : c.Frames - 1;
                    c.Fps = span > 0 && frames > 0 ? frames / span : 0;
                    (c.Before, c.ReadAt, c.Frames) = (c.Last, now, 0);
                }
                // Nothing new: the events may still be on their way (ETW's batches) — keep the rate a moment, then drop it.
                else if ((now - c.ReadAt).TotalSeconds > 2.5) { _chains.Remove(key); continue; }
                if (c.Fps <= 0) continue;
                var k = (key.Pid, key.Source);
                rates[k] = Math.Max(rates.GetValueOrDefault(k), c.Fps);
            }
        }
        // Only the game being played: Shelf's library holds Steam tools too (a dock, Wallpaper Engine) that draw all day
        // behind it. Its frames are its own process's, or, for a game built on Chromium (NW.js, Electron), those of the
        // copy of its exe that draws for the window (the GPU process).
        var result = new Dictionary<int, double>();
        if (front <= 0) return result;
        string? frontPath = PathFor(front);
        double best = 0;
        foreach (var g in rates.GroupBy(r => r.Key.Pid))
        {
            if (g.Key != front && (frontPath == null || !string.Equals(PathFor(g.Key), frontPath, StringComparison.OrdinalIgnoreCase))) continue;
            // the kernel's count only for an app with no DXGI or Direct3D 9 presents of its own
            double app = g.Where(r => r.Key.Source != 0).Select(r => r.Value).DefaultIfEmpty(0).Max();
            best = Math.Max(best, app > 0 ? app : g.Max(r => r.Value));
        }
        if (best >= 1 && IsGame(front, frontWindow)) result[front] = best;
        return result;
    }

    // --- Which apps are games ---

    private readonly Dictionary<int, (string? Path, DateTime Seen)> _paths = new();
    private HashSet<string> _gameExes = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _gameDirs = new();
    private DateTime _gamesReadAt = DateTime.MinValue;
    private bool _shelf;

    /// <summary>
    /// A game. With Shelf installed, one of its games and nothing else: its exe, or anything under its install folder
    /// (for the ones a launcher starts) — the two apps agree on what a game is. Without Shelf, an exe Windows knows as
    /// one (the Game Bar's list, HKCU\System\GameConfigStore), or the app in front while it fills its whole screen,
    /// unless that is the desktop or a browser-style window.
    /// </summary>
    private bool IsGame(int pid, IntPtr frontWindow)
    {
        if ((DateTime.Now - _gamesReadAt).TotalMinutes >= 5) ReadGames();
        if (PathFor(pid) is { } path)
        {
            if (_gameExes.Contains(path)) return true;
            foreach (var dir in _gameDirs)
                if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !_shelf && frontWindow != IntPtr.Zero && FillsScreen(frontWindow);
    }

    /// <summary>A process's exe without its drive or share (<see cref="Ecosystem.NoRoot"/>); pids are reused, so looked up again each minute.</summary>
    private string? PathFor(int pid)
    {
        var now = DateTime.Now;
        if (!_paths.TryGetValue(pid, out var p) || (now - p.Seen).TotalMinutes >= 1)
            _paths[pid] = p = (PathOf(pid) is { } full ? Ecosystem.NoRoot(full) : null, now);
        return p.Path;
    }

    private void ReadGames()
    {
        var now = _gamesReadAt = DateTime.Now;
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        bool shelf = Ecosystem.SisterExe("shelf", "Shelf") != null;
        if (!shelf) try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) exes.Add(Ecosystem.NoRoot(exe));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        foreach (var (exe, dir) in Ecosystem.ShelfGameFolders())
        {
            exes.Add(Ecosystem.NoRoot(exe));
            // a folder only when it is a game's own, not a drive's root
            if (dir is { Length: > 3 }) dirs.Add(Ecosystem.NoRoot(dir).TrimEnd('\\') + "\\");
        }
        foreach (var pid in _paths.Where(kv => (now - kv.Value.Seen).TotalMinutes >= 5).Select(kv => kv.Key).ToList()) _paths.Remove(pid);
        (_gameExes, _gameDirs, _shelf) = (exes, dirs, shelf);
    }

    private static readonly string[] NotGames =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Chrome_WidgetWin_0", "Chrome_WidgetWin_1", "MozillaWindowClass", "ApplicationFrameWindow"];

    private static bool FillsScreen(IntPtr window)
    {
        var cls = new StringBuilder(64);
        GetClassName(window, cls, cls.Capacity);
        if (NotGames.Contains(cls.ToString())) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(window, out var r) || !GetMonitorInfo(MonitorFromWindow(window, 2 /* nearest */), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static string? PathOf(int pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public void Dispose() => _session?.Dispose();
}
