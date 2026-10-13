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

    /// <summary>The catalog's matches for a search (name, id, version), or an empty list when nothing matched.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">winget isn't installed.</exception>
    public static List<(string Name, string Id, string Version)> Search(string query)
    {
        var psi = new ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        // An argument list, not a command line: whatever is typed stays one argument.
        foreach (var arg in new[] { "search", query, "--accept-source-agreements" }) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return ParseTable(output);
    }

    /// <summary>
    /// winget's table: a header, a line of dashes, then rows aligned under the header's words. The header is
    /// in the system's language, so columns are found by position (name, id, version come first), not by name.
    /// </summary>
    public static List<(string Name, string Id, string Version)> ParseTable(string output)
    {
        // A spinner redraws itself with \r before the table: keep what's after the last one on each line.
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r').Split('\r').Last().TrimEnd()).ToList();
        int dashes = lines.FindIndex(l => l.Length > 10 && l.All(c => c == '-'));
        if (dashes < 1) return new();
        string header = lines[dashes - 1];
        var starts = Enumerable.Range(0, header.Length).Where(i => header[i] != ' ' && (i == 0 || header[i - 1] == ' ')).ToList();
        if (starts.Count < 3) return new();
        string Cell(string row, int column)
        {
            int from = starts[column], to = column + 1 < starts.Count ? starts[column + 1] : row.Length;
            return from >= row.Length ? "" : row[from..Math.Min(to, row.Length)].Trim();
        }
        return lines.Skip(dashes + 1)
            .Where(l => l.Length > starts[1])
            .Select(l => (Name: Cell(l, 0), Id: Cell(l, 1), Version: Cell(l, 2)))
            .Where(r => r.Name.Length > 0 && r.Id.Length > 0 && !r.Id.Contains(' '))
            .ToList();
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
