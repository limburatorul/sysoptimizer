using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Sysoptimizer.Models;

namespace Sysoptimizer.Services;

public static class TweakService
{
    public static List<Tweak> GetTweaks() => new()
    {
        new Tweak
        {
            Name = "Windows telemetry",
            Description = "Sets diagnostic data to the lowest level Windows allows: off on Enterprise and Education, Required only on Home and Pro",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0,
            Apply = () => WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
            Revert = () => DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"),
        },
        new Tweak
        {
            Name = "Cortana and web search in Start",
            Description = "Turns off Cortana and web results in the Start menu",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana") == 0,
            Apply = () =>
            {
                WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0);
                WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0);
            },
            Revert = () =>
            {
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana");
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb");
            },
        },
        new Tweak
        {
            Name = "Xbox Game Bar / Game DVR",
            Description = "Stops Game Bar's background recording, which uses CPU/GPU",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0,
            Apply = () => WriteDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0),
            Revert = () => WriteDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1),
        },
        new Tweak
        {
            Name = "Animations and transparency",
            Description = "Disables window animations and transparency for a snappier desktop",
            IsApplied = () => ReadString(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate") == "0",
            Apply = () =>
            {
                WriteString(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0");
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0);
            },
            Revert = () =>
            {
                WriteString(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "1");
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 1);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
            },
        },
        new Tweak
        {
            Name = "Ads and suggestions in Start / lock screen",
            Description = "Turns off suggested apps and promoted content from Windows",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled") == 0,
            Apply = () =>
            {
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0);
            },
            Revert = () =>
            {
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 1);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 1);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 1);
            },
        },
        new Tweak
        {
            Name = "Hibernation",
            Description = "Disables hibernation and removes hiberfil.sys, freeing disk space; Fast Startup and hybrid sleep go with it",
            IsApplied = () => !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "hiberfil.sys")),
            Apply = () => RunPowercfg("/hibernate off"),
            Revert = () => RunPowercfg("/hibernate on"),
        },
        new Tweak
        {
            Name = "Activity history",
            Description = "Stops Windows from recording and syncing your activity timeline",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed") == 0,
            Apply = () =>
            {
                WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
                WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0);
                WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0);
            },
            Revert = () =>
            {
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed");
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities");
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities");
            },
        },
        new Tweak
        {
            Name = "Location tracking",
            Description = "Denies apps and Windows access to your location",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation") == 1,
            Apply = () => WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1),
            Revert = () => DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation"),
        },
        new Tweak
        {
            Name = "Storage Sense",
            Description = "Stops Windows from automatically deleting files it thinks are junk",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01") == 0,
            Apply = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0),
            Revert = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1),
        },
        new Tweak
        {
            Name = "Background apps",
            Description = "Blocks apps from running or sending notifications in the background",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground") == 2,
            Apply = () => WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2),
            Revert = () => DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground"),
        },
        new Tweak
        {
            Name = "Notification toasts",
            Description = "Turns off popup notifications from apps and Windows",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled") == 0,
            Apply = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0),
            Revert = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 1),
        },
        new Tweak
        {
            Name = "Power throttling",
            Description = "Stops Windows from deliberately slowing down background processes to save power",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff") == 1,
            Apply = () => WriteDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1),
            Revert = () => DeleteValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff"),
        },
        new Tweak
        {
            Name = "Show hidden files and extensions",
            Description = "Explorer shows hidden files and real file extensions instead of guessing for you",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden") == 1,
            Apply = () =>
            {
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0);
            },
            Revert = () =>
            {
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2);
                WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1);
            },
        },
        new Tweak
        {
            Name = "\"End task\" on taskbar right-click",
            Description = "Adds End Task to a taskbar app's right-click menu, without opening Task Manager",
            IsApplied = () => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask") == 1,
            Apply = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1),
            Revert = () => WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 0),
        },
        new Tweak
        {
            Name = "Classic right-click menu (Windows 11)",
            Description = "Brings back the full context menu instead of the shortened Windows 11 one",
            IsApplied = () => Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32") != null,
            Apply = () => WriteString(Registry.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", ""),
            Revert = () => Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", throwOnMissingSubKey: false),
        },
        new Tweak
        {
            Name = "Windows Recall",
            Description = "Disables Recall's continuous screen snapshotting (Copilot+ PCs)",
            IsApplied = () => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis") == 1,
            Apply = () => WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            Revert = () => DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis"),
        },
    };

    public static void ExportProfile(string path, List<Tweak> tweaks)
    {
        var state = tweaks.ToDictionary(t => t.Name, t => t.IsApplied());
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <returns>Log lines describing what changed.</returns>
    public static List<string> ImportProfile(string path, List<Tweak> tweaks)
    {
        var log = new List<string>();
        Dictionary<string, bool>? state;
        try { state = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)); }
        catch (Exception ex) { log.Add($"Could not read profile — {ex.Message}"); return log; }
        if (state == null) return log;

        foreach (var tweak in tweaks)
        {
            if (!state.TryGetValue(tweak.Name, out bool wantApplied)) continue;
            bool isApplied = tweak.IsApplied();
            if (wantApplied == isApplied) continue;
            try
            {
                if (wantApplied) tweak.Apply(); else tweak.Revert();
                log.Add($"{tweak.Name}: {(wantApplied ? "applied" : "reverted")}.");
            }
            catch (Exception ex)
            {
                log.Add($"{tweak.Name}: error — {ex.Message}");
            }
        }
        return log;
    }

    /// <summary>Restart Explorer for the tweaks that only take effect on its next launch (hidden files, classic menu, taskbar).</summary>
    public static void RestartExplorer()
    {
        foreach (var proc in Process.GetProcessesByName("explorer")) { try { proc.Kill(); } catch { } }
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("WinDir") ?? @"C:\Windows", "explorer.exe")) { UseShellExecute = true }); }
        catch { }
    }

    public static (bool ok, string message) CreateRestorePoint()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"Checkpoint-Computer -Description 'Sysoptimizer' -RestorePointType MODIFY_SETTINGS\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000);
            return proc.ExitCode == 0 ? (true, "Restore point created.") : (false, output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        var value = key?.GetValue(name);
        return value is int i ? i : null;
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void WriteDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void WriteString(RegistryKey root, string path, string name, string value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteValue(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", arguments) { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }
}
