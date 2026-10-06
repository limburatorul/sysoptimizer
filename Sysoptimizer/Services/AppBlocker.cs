using System.IO;
using Microsoft.Win32;

namespace Sysoptimizer.Services;

/// <summary>
/// Blocks an executable from starting via Image File Execution Options: Windows launches the "Debugger"
/// value instead of the exe, and systray.exe is a stub that exits at once — so the app just never opens.
/// Applies to that file name anywhere on disk. Fully reversible: Unblock deletes exactly what Block wrote,
/// and the marker value keeps us from ever touching IFEO entries other tools created.
/// </summary>
public static class AppBlocker
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Marker = "SysoptimizerBlocked";
    private static readonly string Stub = Path.Combine(Environment.SystemDirectory, "systray.exe");

    /// <returns>Null on success, otherwise why it was refused.</returns>
    public static string? Block(string exePath)
    {
        string exe = Path.GetFileName(exePath);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Blocking by file name hits every copy, so anything Windows itself ships could brick the session.
        if (exePath.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            return "it's part of Windows";
        if (exe.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            return "that's Sysoptimizer itself";

        using var key = Registry.LocalMachine.CreateSubKey($@"{IfeoPath}\{exe}");
        if (key.GetValue("Debugger") != null && key.GetValue(Marker) == null)
            return "another tool already controls this exe's launch";
        key.SetValue("Debugger", Stub);
        key.SetValue(Marker, 1, RegistryValueKind.DWord);
        return null;
    }

    public static void Unblock(string exe)
    {
        using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath, writable: true);
        using var key = ifeo?.OpenSubKey(exe, writable: true);
        if (key?.GetValue(Marker) == null) return;
        key.DeleteValue("Debugger", throwOnMissingValue: false);
        key.DeleteValue(Marker, throwOnMissingValue: false);
        bool empty = key.ValueCount == 0 && key.SubKeyCount == 0;
        key.Dispose();
        if (empty) ifeo!.DeleteSubKey(exe, throwOnMissingSubKey: false);
    }

    public static List<string> GetBlocked()
    {
        using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath);
        if (ifeo == null) return new();
        return ifeo.GetSubKeyNames().Where(name =>
        {
            using var key = ifeo.OpenSubKey(name);
            return key?.GetValue(Marker) != null;
        }).OrderBy(n => n).ToList();
    }
}
