using Microsoft.Win32;

namespace Sysoptimizer.Services;

public record InstalledApp(string Name, string? Version, string? Publisher, string UninstallString, string? InstallLocation);

public static class UninstallService
{
    public static List<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in roots)
        {
            using var uninstallKey = hive.OpenSubKey(path);
            if (uninstallKey == null) continue;
            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var key = uninstallKey.OpenSubKey(subKeyName);
                if (key == null) continue;
                string? name = key.GetValue("DisplayName") as string;
                string? uninstallString = key.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstallString)) continue;
                if ((key.GetValue("SystemComponent") as int?) == 1) continue;
                if (key.GetValue("ParentKeyName") != null) continue; // an update/patch, not a standalone app

                apps.Add(new InstalledApp(name, key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string,
                    uninstallString, key.GetValue("InstallLocation") as string));
            }
        }

        return apps.GroupBy(a => a.Name).Select(g => g.First()).OrderBy(a => a.Name).ToList();
    }
}
