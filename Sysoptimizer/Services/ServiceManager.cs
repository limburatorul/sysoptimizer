using System.Diagnostics;

namespace Sysoptimizer.Services;

public static class ServiceManager
{
    // DefaultStart is Windows' own startup type, what "Re-enable" puts back.
    public static readonly (string Key, string Name, string Description, string DefaultStart)[] KnownServices =
    {
        ("DiagTrack", "Connected User Experiences and Telemetry", "Sends diagnostic and usage data to Microsoft", "auto"),
        ("dmwappushservice", "WAP Push Message Routing", "Device-management push service, rarely needed at home", "demand"),
        ("SysMain", "SysMain (Superfetch)", "Preloads often-used apps into RAM; helps most on hard drives, less on SSDs", "auto"),
        ("WSearch", "Windows Search", "Indexes files so search is fast; without it, Start and Explorer search are slower", "delayed-auto"),
        ("Fax", "Fax", "Fax service, almost never used", "demand"),
        ("RemoteRegistry", "Remote Registry", "Allows the registry to be edited remotely", "demand"),
        ("MapsBroker", "Downloaded Maps Manager", "Updates downloaded offline maps", "delayed-auto"),
        ("PrintNotify", "Printer Extensions and Notifications", "Printer notifications; not needed without a printer", "demand"),
        ("RetailDemo", "Retail Demo Service", "Store-display demo mode", "demand"),
        ("WerSvc", "Windows Error Reporting Service", "Sends error reports to Microsoft", "demand"),
    };

    public static string GetStatus(string serviceName)
    {
        var output = RunSc($"query \"{serviceName}\"");
        if (output.Contains("does not exist") || output.Length == 0) return "Absent";
        if (output.Contains("RUNNING")) return "Running";
        if (output.Contains("STOPPED")) return "Stopped";
        return "Unknown";
    }

    public static void StopAndDisable(string serviceName)
    {
        RunSc($"stop \"{serviceName}\"");
        RunSc($"config \"{serviceName}\" start= disabled");
    }

    public static void EnableAndStart(string serviceName)
    {
        var start = KnownServices.FirstOrDefault(s => s.Key == serviceName).DefaultStart ?? "demand";
        RunSc($"config \"{serviceName}\" start= {start}");
        RunSc($"start \"{serviceName}\"");
    }

    private static string RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
