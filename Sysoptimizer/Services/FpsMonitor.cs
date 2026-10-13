using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Sysoptimizer.Services;

/// <summary>
/// Frames per second of every app that draws, the way PresentMon measures it: an ETW session counts each
/// app's presents — the moment a finished frame is handed to Windows. The graphics kernel's own Present
/// event covers DirectX, Vulkan and OpenGL alike; DXGI's and Direct3D 9's are counted too, for the apps
/// whose frames take another path, and the highest of the three is the app's rate. Needs admin.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "Sysoptimizer-FPS";
    private TraceEventSession? _session;
    // Presents per (process, source) since the last read; the sources are counted apart, never added up.
    private ConcurrentDictionary<(int Pid, int Source), int> _counts = new();
    private DateTime _since = DateTime.UtcNow;

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
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Error = ex.Message;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int source = e.ProviderName switch
        {
            "Microsoft-Windows-DxgKrnl" when e.EventName == "Present" => 0,
            "Microsoft-Windows-DXGI" when e.Opcode == TraceEventOpcode.Start && e.EventName.StartsWith("Present") => 1,
            "Microsoft-Windows-D3D9" when e.Opcode == TraceEventOpcode.Start && e.EventName.StartsWith("Present") => 2,
            _ => -1,
        };
        if (source >= 0 && e.ProcessID > 0) _counts.AddOrUpdate((e.ProcessID, source), 1, (_, n) => n + 1);
    }

    /// <summary>Frames per second of each process that drew anything since the last call.</summary>
    public Dictionary<int, double> Read()
    {
        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.2, (now - _since).TotalSeconds);
        var counts = Interlocked.Exchange(ref _counts, new());
        _since = now;
        return counts.GroupBy(kv => kv.Key.Pid).ToDictionary(g => g.Key, g => g.Max(kv => kv.Value) / seconds);
    }

    public void Dispose() => _session?.Dispose();
}
