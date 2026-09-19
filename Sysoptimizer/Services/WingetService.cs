using System.Diagnostics;

namespace Sysoptimizer.Services;

public static class WingetService
{
    public static readonly (string Name, string Id)[] PopularApps =
    {
        ("7-Zip", "7zip.7zip"),
        ("Google Chrome", "Google.Chrome"),
        ("Mozilla Firefox", "Mozilla.Firefox"),
        ("VLC media player", "VideoLAN.VLC"),
        ("Notepad++", "Notepad++.Notepad++"),
        ("Visual Studio Code", "Microsoft.VisualStudioCode"),
        ("PowerToys", "Microsoft.PowerToys"),
        ("Discord", "Discord.Discord"),
        ("Steam", "Valve.Steam"),
        ("qBittorrent", "qBittorrent.qBittorrent"),
        ("Malwarebytes", "Malwarebytes.Malwarebytes"),
        ("WinRAR", "RARLab.WinRAR"),
    };

    public static bool IsWingetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("winget.exe", "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool Install(string id, out string output)
    {
        return Run($"install --id {id} -e --silent --accept-source-agreements --accept-package-agreements", out output);
    }

    public static bool UpgradeAll(out string output)
    {
        return Run("upgrade --all --silent --accept-source-agreements --accept-package-agreements", out output);
    }

    private static bool Run(string arguments, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("winget.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(180000);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }
}
