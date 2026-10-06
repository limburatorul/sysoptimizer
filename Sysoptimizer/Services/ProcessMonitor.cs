using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sysoptimizer.Services;

/// <param name="Cpu">Share of the whole machine, like Task Manager (0–100 across all cores).</param>
public sealed record ProcessUsage(string Name, int Instances, double Cpu, double RamMB, double Gpu, string? Path, string? Description, string? Company);

/// <summary>Per-app usage, every instance of an executable rolled into one row (chrome × 30 → one "chrome").</summary>
public sealed class ProcessMonitor
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastAt = DateTime.UtcNow;
    private readonly Dictionary<int, string?> _paths = new();
    private readonly Dictionary<string, (string? Description, string? Company)> _info = new(StringComparer.OrdinalIgnoreCase);

    public List<ProcessUsage> Sample(IReadOnlyDictionary<int, double> gpuByPid)
    {
        var now = DateTime.UtcNow;
        double elapsed = Math.Max(0.001, (now - _lastAt).TotalSeconds);
        var cpuNow = new Dictionary<int, TimeSpan>();
        var groups = new Dictionary<string, (int Count, double Cpu, double Ram, double Gpu, string? Path)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // Idle is the absence of work, not a consumer
                double cpu = 0;
                try
                {
                    var total = p.TotalProcessorTime;
                    cpuNow[p.Id] = total;
                    if (_lastCpu.TryGetValue(p.Id, out var prev))
                        cpu = Math.Max(0, (total - prev).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
                }
                catch { } // protected processes (csrss, anti-cheat…) refuse a handle even to an admin

                double ram = 0;
                try { ram = p.WorkingSet64 / 1048576.0; } catch { }

                string name = p.ProcessName;
                var g = groups.GetValueOrDefault(name);
                groups[name] = (g.Count + 1, g.Cpu + cpu, g.Ram + ram, g.Gpu + gpuByPid.GetValueOrDefault(p.Id), g.Path ?? PathOf(p.Id));
            }
        }

        foreach (var pid in _paths.Keys.Where(pid => !cpuNow.ContainsKey(pid)).ToList()) _paths.Remove(pid);
        _lastCpu = cpuNow;
        _lastAt = now;

        return groups.Select(kv =>
        {
            var (description, company) = kv.Value.Path is { } path ? InfoOf(path) : (null, null);
            return new ProcessUsage(kv.Key, kv.Value.Count, Math.Min(100, kv.Value.Cpu), kv.Value.Ram, Math.Min(100, kv.Value.Gpu), kv.Value.Path, description, company);
        }).ToList();
    }

    private string? PathOf(int pid)
    {
        if (_paths.TryGetValue(pid, out var cached)) return cached;
        string? path = null;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString();
            CloseHandle(handle);
        }
        return _paths[pid] = path;
    }

    /// <summary>The "what is this?" answer: the exe's own description and publisher from its version info.</summary>
    private (string?, string?) InfoOf(string path)
    {
        if (_info.TryGetValue(path, out var cached)) return cached;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return _info[path] = (Blank(version.FileDescription), Blank(version.CompanyName));
        }
        catch { return _info[path] = (null, null); }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>What goes into the history every 10 seconds: the heaviest few by each measure, not all ~300.</summary>
    public static List<ProcessUsage> ForHistory(List<ProcessUsage> all) =>
        all.OrderByDescending(p => p.Cpu).Take(8).Where(p => p.Cpu >= 0.5)
           .Concat(all.OrderByDescending(p => p.RamMB).Take(5))
           .Concat(all.OrderByDescending(p => p.Gpu).Take(5).Where(p => p.Gpu >= 0.5))
           .DistinctBy(p => p.Name).ToList();
}
