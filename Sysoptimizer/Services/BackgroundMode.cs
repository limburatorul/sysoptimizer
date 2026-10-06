using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Sysoptimizer.Services;

/// <summary>
/// "Record in background": start hidden in the tray at every logon so the history has no gaps. The app
/// needs admin, and Windows silently skips elevated apps in the Run key — a scheduled task with
/// "highest privileges" is the supported way to start one at logon without a UAC prompt.
/// </summary>
public static class BackgroundMode
{
    private const string TaskName = "Sysoptimizer";
    private const string SettingsPath = @"Software\Sysoptimizer";
    public const string TrayArgument = "--tray";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsPath);
            return key?.GetValue("Background") is 1;
        }
    }

    /// <returns>Null on success, otherwise schtasks' error.</returns>
    public static string? Enable()
    {
        // Built as XML rather than schtasks flags: the flag form leaves the defaults that would kill a
        // tray app — stop after 3 days, don't start on battery, stop when unplugged.
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(Environment.ProcessPath)}</Command><Arguments>{TrayArgument}</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "sysoptimizer-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var (ok, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (!ok) return output;
        }
        finally { File.Delete(file); }
        Save(true);
        return null;
    }

    public static void Disable()
    {
        Schtasks($"/Delete /TN \"{TaskName}\" /F");
        Save(false);
    }

    private static void Save(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsPath);
        key.SetValue("Background", on ? 1 : 0, RegistryValueKind.DWord);
    }

    private static (bool, string) Schtasks(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode == 0, output.Trim());
    }
}
