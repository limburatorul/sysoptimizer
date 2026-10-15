using System.IO;

namespace Sysoptimizer.Services;

public static class CleanupService
{
    public static readonly (string Name, string Description, Func<string?> Path)[] Targets =
    {
        ("User temp", "Temporary files from %TEMP%", () => System.Environment.GetEnvironmentVariable("TEMP")),
        ("Windows temp", "C:\\Windows\\Temp", () => Path.Combine(System.Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "Temp")),
        ("Prefetch", "Application startup cache", () => Path.Combine(System.Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "Prefetch")),
        ("Windows Update cache", "Already-installed updates, downloaded", () => Path.Combine(System.Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "SoftwareDistribution", "Download")),
        ("Delivery Optimization", "P2P update-sharing cache", () => Path.Combine(System.Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "SoftwareDistribution", "DeliveryOptimization")),
        ("Error reports", "Windows Error Reporting", () => Path.Combine(System.Environment.GetEnvironmentVariable("ProgramData") ?? "C:\\ProgramData", "Microsoft", "Windows", "WER")),
        ("Thumbnail cache", "Explorer thumbnail cache", () => Path.Combine(System.Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Microsoft", "Windows", "Explorer")),
    };

    public static long GetSize(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var file in EnumerateFilesSafe(folder))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    public static (int deleted, int skipped, long freed) Clean(string? folder)
    {
        int deleted = 0, skipped = 0;
        long freed = 0;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return (0, 0, 0);

        foreach (var file in EnumerateFilesSafe(folder))
        {
            long len = 0;
            try { len = new FileInfo(file).Length; File.Delete(file); deleted++; freed += len; }
            catch { skipped++; }
        }
        foreach (var dir in EnumerateDirsSafe(folder))
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
        return (deleted, skipped, freed);
    }

    // The enumeration is lazy: a folder it may not open fails while it's being walked, not when it starts, so
    // a try around the call caught nothing and one locked folder in %TEMP% crashed the app. Inaccessible
    // folders are skipped as they come, and links aren't followed — cleaning must never reach outside the folder.
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static IEnumerable<string> EnumerateFilesSafe(string root) => Directory.EnumerateFiles(root, "*", Walk);

    private static IEnumerable<string> EnumerateDirsSafe(string root) =>
        Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true });

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
