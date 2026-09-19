using Microsoft.Win32;

namespace Sysoptimizer.Services;

public static class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string BackupPath = @"Software\Sysoptimizer\DisabledStartup";

    public record StartupItem(string Name, string Command, string Location);

    public static List<StartupItem> GetItems()
    {
        var items = new List<StartupItem>();

        foreach (var (root, location) in new[] { (Registry.CurrentUser, "Current user"), (Registry.LocalMachine, "All users") })
        {
            using var key = root.OpenSubKey(RunPath);
            if (key == null) continue;
            foreach (var name in key.GetValueNames())
                items.Add(new StartupItem(name, key.GetValue(name) as string ?? "", location));
        }

        using var backup = Registry.CurrentUser.OpenSubKey(BackupPath);
        if (backup != null)
        {
            foreach (var name in backup.GetValueNames())
                items.Add(new StartupItem(name, backup.GetValue(name) as string ?? "", "Disabled"));
        }

        return items.OrderBy(i => i.Name).ToList();
    }

    public static void Disable(StartupItem item)
    {
        var root = item.Location == "All users" ? Registry.LocalMachine : Registry.CurrentUser;
        using var runKey = root.OpenSubKey(RunPath, writable: true);
        string? command = runKey?.GetValue(item.Name) as string;
        if (command == null) return;

        using var backupKey = Registry.CurrentUser.CreateSubKey(BackupPath);
        // Remember which hive it came from so Enable() can put it back in the same place.
        backupKey.SetValue(item.Name, $"{item.Location}|{command}", RegistryValueKind.String);
        runKey!.DeleteValue(item.Name, throwOnMissingValue: false);
    }

    public static void Enable(StartupItem item)
    {
        using var backupKey = Registry.CurrentUser.OpenSubKey(BackupPath, writable: true);
        string? stored = backupKey?.GetValue(item.Name) as string;
        if (stored == null) return;

        var parts = stored.Split('|', 2);
        var (location, command) = parts.Length == 2 ? (parts[0], parts[1]) : ("Current user", parts[0]);
        var root = location == "All users" ? Registry.LocalMachine : Registry.CurrentUser;

        using var runKey = root.CreateSubKey(RunPath);
        runKey.SetValue(item.Name, command, RegistryValueKind.String);
        backupKey!.DeleteValue(item.Name, throwOnMissingValue: false);
    }
}
