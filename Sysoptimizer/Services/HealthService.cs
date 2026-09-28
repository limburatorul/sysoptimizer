using System.IO;
using System.Runtime.InteropServices;

namespace Sysoptimizer.Services;

public record HealthReport(int Score, List<string> Issues);

/// <summary>The "how's my PC doing" summary every paid optimizer leads with — built from the same
/// checks the other tabs already expose, not a separate scanning engine.</summary>
public static class HealthService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static HealthReport Check()
    {
        var issues = new List<string>();
        int score = 100;

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            double freeFrac = drive.AvailableFreeSpace / (double)drive.TotalSize;
            if (freeFrac < 0.10) { issues.Add("Less than 10% free space on the system drive"); score -= 20; }
            else if (freeFrac < 0.20) { issues.Add("Less than 20% free space on the system drive"); score -= 10; }
        }
        catch { }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.dwMemoryLoad >= 90)
        {
            issues.Add($"Memory is {mem.dwMemoryLoad}% full");
            score -= 10;
        }

        long junk = CleanupService.Targets.Sum(t => CleanupService.GetSize(t.Path()));
        if (junk > 2L * 1024 * 1024 * 1024) { issues.Add($"{CleanupService.FormatSize(junk)} of temporary files"); score -= 15; }
        else if (junk > 500L * 1024 * 1024) { issues.Add($"{CleanupService.FormatSize(junk)} of temporary files"); score -= 7; }

        int runningBloat = ServiceManager.KnownServices.Count(s => ServiceManager.GetStatus(s.Key) == "Running");
        if (runningBloat > 0)
        {
            issues.Add($"{runningBloat} background service{(runningBloat == 1 ? "" : "s")} that can be disabled");
            score -= Math.Min(20, runningBloat * 4);
        }

        int startupCount = StartupService.GetItems().Count(i => i.Location != "Disabled");
        if (startupCount > 10) { issues.Add($"{startupCount} apps launch at startup"); score -= 10; }

        if (issues.Count == 0) issues.Add("No issues found.");
        return new HealthReport(Math.Clamp(score, 0, 100), issues);
    }

    /// <summary>Cleans every target, trims memory, and stops every bloat service currently running. Reversible: services can be re-enabled from the Services tab.</summary>
    public static List<string> Optimize()
    {
        var log = new List<string>();
        long totalFreed = 0;
        foreach (var target in CleanupService.Targets)
        {
            var (deleted, _, freed) = CleanupService.Clean(target.Path());
            totalFreed += freed;
            if (deleted > 0) log.Add($"{target.Name}: {CleanupService.FormatSize(freed)} freed.");
        }
        if (totalFreed > 0) log.Add($"Total freed: {CleanupService.FormatSize(totalFreed)}.");

        var (trimmed, memFreed) = MemoryService.TrimAll();
        log.Add($"Memory freed from {trimmed} processes: {CleanupService.FormatSize(memFreed)}.");

        foreach (var (key, name, _, _) in ServiceManager.KnownServices)
        {
            if (ServiceManager.GetStatus(key) != "Running") continue;
            ServiceManager.StopAndDisable(key);
            log.Add($"{name}: stopped and disabled.");
        }

        return log;
    }
}
