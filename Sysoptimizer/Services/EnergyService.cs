using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Sysoptimizer.Services;

/// <summary>
/// What Windows itself measured each app using — its energy estimator's records (SRUM), read through
/// <c>powercfg /srumutil</c>, which needs admin (the app runs elevated). Windows keeps about a week of them
/// and writes them roughly once an hour; they include the screen, disk and network share, not just CPU/GPU.
/// Free of WPF so the self-test can exercise the parsing.
/// </summary>
public static class EnergyService
{
    private static List<(DateTime Time, string App, long MilliJoules)>? _rows;
    private static DateTime _readAt;
    private static readonly object Gate = new();

    /// <summary>Watt-hours per app between two UTC times, most first.</summary>
    /// <exception cref="IOException">powercfg failed or wrote nothing.</exception>
    public static List<(string App, double Wh)> Top(DateTime fromUtc, DateTime toUtc, int limit = 15)
    {
        List<(DateTime Time, string App, long MilliJoules)> rows;
        lock (Gate)
        {
            // The dump takes ~10 s and Windows only adds to it hourly: one every 10 minutes is plenty.
            if (_rows == null || DateTime.UtcNow - _readAt > TimeSpan.FromMinutes(10)) (_rows, _readAt) = (Dump(), DateTime.UtcNow);
            rows = _rows;
        }
        return rows.Where(r => r.Time >= fromUtc && r.Time <= toUtc)
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.Key, Wh: g.Sum(r => r.MilliJoules) / 3.6e6))
            .Where(t => t.Wh >= 0.01).OrderByDescending(t => t.Wh).Take(limit).ToList();
    }

    private static List<(DateTime, string, long)> Dump()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"sysoptimizer-energy-{Guid.NewGuid():N}.csv");
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe") { CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "/srumutil", "/output", csv, "/csv" }) psi.ArgumentList.Add(arg);
            using (var process = Process.Start(psi)!)
                if (!process.WaitForExit(60_000)) { process.Kill(); throw new IOException("powercfg took over a minute"); }
            if (!File.Exists(csv)) throw new IOException("powercfg wrote nothing — it needs admin rights");
            using var reader = new StreamReader(csv);
            return Parse(reader);
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException($"couldn't run powercfg — {ex.Message}"); }
        finally
        {
            try { File.Delete(csv); } catch (IOException) { }
        }
    }

    /// <summary>powercfg's CSV → (UTC time, app, mJ). The EMI_ rows are the hardware meters themselves, not apps.</summary>
    public static List<(DateTime, string, long)> Parse(TextReader reader)
    {
        var header = (reader.ReadLine() ?? "").Split(',').Select(h => h.Trim()).ToList();
        int app = header.IndexOf("AppId"), time = header.IndexOf("TimeStamp"), total = header.IndexOf("TotalEnergyConsumption");
        if (app < 0 || time < 0 || total < 0) throw new IOException("powercfg's energy table isn't in the expected format");
        var rows = new List<(DateTime, string, long)>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length != header.Count || f[app].StartsWith("EMI_")) continue; // a comma in a path shifts the columns: skip it
            if (!DateTime.TryParseExact(f[time].Trim(), "yyyy-MM-ddTHH:mm:ss.ffff", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                || !long.TryParse(f[total].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long mj) || mj == 0) continue;
            rows.Add((at, AppName(f[app].Trim()), mj));
        }
        return rows;
    }

    /// <summary>\Device\…\chrome.exe → chrome (as the Processes tab names it); Claude_2.1.0_x64__pzs… → Claude.</summary>
    private static string AppName(string id) =>
        id.Contains('\\') ? Path.GetFileNameWithoutExtension(id[(id.LastIndexOf('\\') + 1)..])
        : id.Contains("__") ? id[..id.IndexOf('_')]
        : id;
}
