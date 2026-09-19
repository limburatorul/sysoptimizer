using System.Diagnostics;

namespace Sysoptimizer.Services;

public static class ServiceManager
{
    public static readonly (string Key, string Name, string Description)[] KnownServices =
    {
        ("DiagTrack", "Connected User Experiences and Telemetry", "Sends diagnostic and usage data to Microsoft"),
        ("dmwappushservice", "WAP Push Message Routing", "Device-management push service, rarely needed at home"),
        ("SysMain", "SysMain (Superfetch)", "Preloads apps into RAM — useful on HDD, useless or harmful on SSD"),
        ("WSearch", "Windows Search", "Indexes files for fast search; a constant CPU/disk cost"),
        ("Fax", "Fax", "Fax service, almost never used"),
        ("RemoteRegistry", "Remote Registry", "Allows the registry to be edited remotely"),
        ("MapsBroker", "Downloaded Maps Manager", "Updates downloaded offline maps"),
        ("PrintNotify", "Printer Extensions and Notifications", "Printer notifications; useless without a printer"),
        ("RetailDemo", "Retail Demo Service", "Store-display demo mode"),
        ("WerSvc", "Windows Error Reporting Service", "Sends error reports to Microsoft"),
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
        RunSc($"config \"{serviceName}\" start= demand");
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
