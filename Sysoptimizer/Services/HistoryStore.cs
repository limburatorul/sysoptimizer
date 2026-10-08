using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Sysoptimizer.Services;

/// <param name="DiskMBs">Read + write across all disks, MB/s.</param>
/// <param name="NetMbps">Down + up across all adapters, Mbit/s.</param>
/// <param name="CpuWatts">CPU package power draw, W.</param>
/// <param name="GpuWatts">Graphics card power draw, W.</param>
/// <param name="FanRpm">The fastest fan's speed, RPM.</param>
/// <param name="BatteryPct">Battery charge, %; unknown on a desktop.</param>
public readonly record struct SysSample(DateTime Time, float Cpu, float Mem, float Gpu, float CpuTemp, float GpuTemp,
    float DiskMBs = float.NaN, float NetMbps = float.NaN, float CpuWatts = float.NaN, float GpuWatts = float.NaN, float FanRpm = float.NaN, float BatteryPct = float.NaN);
public readonly record struct ProcSample(DateTime Time, string Name, float Cpu, float RamMB, float Gpu);
/// <param name="Kind">launch (Detail = app), sleep, wake, start (Detail = boot time, unix seconds).</param>
public readonly record struct HistoryEvent(DateTime Time, string Kind, string Detail);
public sealed record ProcessTotal(string Name, double AvgCpu, double PeakCpu, double AvgRamMB, double PeakRamMB, double AvgGpu, double PeakGpu, int Seen);
public sealed record HistoryData(List<SysSample> System, List<ProcSample> Processes, List<HistoryEvent> Events);

/// <summary>
/// The recorded history: one plain-text file per local day under ProgramData (writable by the elevated
/// app, readable by the non-elevated MCP server). Lines:
///   S,unixSeconds,cpu,mem,gpu,cpuTemp,gpuTemp,diskMBs,netMbps,cpuW,gpuW,fanRpm,battery%
///                                                 — every second (blank when unknown; older files end
///                                                   earlier: at gpuTemp before 1.5, at netMbps before 1.6)
///   P,unixSeconds,name,cpu,ramMB,gpu              — the top processes, every 10 seconds
///   E,unixSeconds,kind,detail                     — app launches, sleep/wake, app start (with boot time)
/// Today's file is appended and flushed line by line, so a hard reset loses at most a torn last line,
/// which the reader skips. A finished day is gzipped and gets a per-minute summary beside it in the same
/// format (S = the minute's peaks, P = the minute's top apps), so weeks and months load in a blink.
/// Shared verbatim with Sysoptimizer.Mcp — keep it free of WPF.
/// </summary>
public static class HistoryStore
{
    public const int MaxRetentionDays = 366;
    /// <summary>Set by the app from the user's choice; days older than this are deleted at each day change.</summary>
    public static int RetentionDays { get; set; } = 90;
    /// <summary>Settable for the self-test, which works in a temp folder.</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sysoptimizer", "history");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static DateTime _writerDay;

    // --- writing ---

    public static void Write(SysSample sample, IReadOnlyCollection<ProcSample>? processes = null)
    {
        lock (Gate)
        {
            var w = WriterFor(sample.Time);
            long t = Unix(sample.Time);
            w.WriteLine($"S,{t},{F(sample.Cpu)},{F(sample.Mem)},{F(sample.Gpu)},{F(sample.CpuTemp)},{F(sample.GpuTemp)},{F(sample.DiskMBs)},{F(sample.NetMbps)},"
                + $"{F(sample.CpuWatts)},{F(sample.GpuWatts)},{F(sample.FanRpm)},{F(sample.BatteryPct)}");
            if (processes == null) return;
            foreach (var p in processes)
                w.WriteLine($"P,{t},{Clean(p.Name)},{F(p.Cpu)},{F(p.RamMB)},{F(p.Gpu)}");
        }
    }

    public static void WriteEvent(string kind, string detail = "", DateTime? at = null)
    {
        lock (Gate)
        {
            var time = at ?? DateTime.UtcNow;
            WriterFor(DateTime.UtcNow).WriteLine($"E,{Unix(time)},{kind},{Clean(detail)}");
        }
    }

    private static StreamWriter WriterFor(DateTime time)
    {
        var day = time.ToLocalTime().Date;
        if (_writer != null && day == _writerDay) return _writer;
        _writer?.Dispose();
        Directory.CreateDirectory(Folder);
        Maintain(day);
        var stream = new FileStream(RawPath(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writerDay = day;
        return _writer;
    }

    private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
    private static string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.#", Inv);
    private static string Clean(string s) => s.Replace(',', '_').Replace('\n', ' ');

    private static string RawPath(DateTime day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.csv");
    private static string MinutePath(DateTime day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.min.csv");

    /// <summary>Gzips and summarizes every finished day still in plain text; deletes days past retention.</summary>
    public static void Maintain(DateTime today)
    {
        foreach (var file in Directory.GetFiles(Folder))
        {
            string name = Path.GetFileName(file);
            if (!DateTime.TryParseExact(name[..Math.Min(10, name.Length)], "yyyy-MM-dd", Inv, DateTimeStyles.None, out var day)) continue;
            try
            {
                if (day < today.AddDays(-RetentionDays)) { File.Delete(file); continue; }
                if (day < today && name == $"{day:yyyy-MM-dd}.csv")
                {
                    EnsureMinuteSummary(day);
                    using (var source = File.OpenRead(file))
                    using (var target = new GZipStream(File.Create(file + ".gz"), CompressionLevel.SmallestSize))
                        source.CopyTo(target);
                    File.Delete(file);
                }
            }
            catch (IOException) { } // a reader has it open right now; try again on the next day change
        }
    }

    // --- reading ---

    /// <summary>Every recorded line in the range, at full resolution (1 s; apps every 10 s, every 2 s during a spike).</summary>
    public static HistoryData Read(DateTime from, DateTime to)
    {
        var data = new HistoryData(new(), new(), new());
        for (var day = from.ToLocalTime().Date; day <= to.ToLocalTime().Date; day = day.AddDays(1))
        {
            using var reader = OpenRaw(day);
            if (reader != null) Parse(reader, Unix(from), Unix(to), data, systemOnly: false);
        }
        return data;
    }

    /// <summary>One whole local day, per second or per minute — the unit the History tab caches to scroll smoothly.</summary>
    public static HistoryData ReadDay(DateTime day, bool minutes)
    {
        var data = new HistoryData(new(), new(), new());
        using var reader = minutes ? OpenMinutes(day) : OpenRaw(day);
        if (reader != null) Parse(reader, long.MinValue, long.MaxValue, data, systemOnly: false);
        return data;
    }

    private static readonly Dictionary<DateTime, List<SysSample>> MinuteCache = new();

    /// <summary>
    /// The range at one row per minute: S = that minute's peaks, P = its top apps at their peak, E = the
    /// events. An index for long ranges — the per-second data stays on disk for the whole retention. Finished days come from their summary file; today is summarized on the fly.
    /// </summary>
    public static HistoryData ReadMinutes(DateTime from, DateTime to, bool systemOnly = false)
    {
        var data = new HistoryData(new(), new(), new());
        long fromT = Unix(from), toT = Unix(to);
        var today = DateTime.Now.Date;
        for (var day = from.ToLocalTime().Date; day <= to.ToLocalTime().Date; day = day.AddDays(1))
        {
            if (systemOnly && day < today)
            {
                // The navigator re-reads weeks or months at a time — keep finished days' rows in memory (~35 KB/day).
                List<SysSample>? cached;
                lock (MinuteCache)
                    if (!MinuteCache.TryGetValue(day, out cached))
                    {
                        var dayData = new HistoryData(new(), new(), new());
                        using (var r = OpenMinutes(day)) if (r != null) Parse(r, long.MinValue, long.MaxValue, dayData, systemOnly: true);
                        MinuteCache[day] = cached = dayData.System;
                    }
                data.System.AddRange(cached.Where(s => s.Time >= from.ToUniversalTime() && s.Time <= to.ToUniversalTime()));
                continue;
            }
            using var reader = OpenMinutes(day);
            if (reader != null) Parse(reader, fromT, toT, data, systemOnly);
        }
        return data;
    }

    private static StreamReader? OpenMinutes(DateTime day)
    {
        if (day < DateTime.Now.Date)
        {
            EnsureMinuteSummary(day);
            return File.Exists(MinutePath(day)) ? new StreamReader(Shared(MinutePath(day))) : null;
        }
        using var raw = OpenRaw(day);
        return raw == null ? null : new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', Summarize(raw)))));
    }

    private static void EnsureMinuteSummary(DateTime day)
    {
        if (File.Exists(MinutePath(day))) return;
        using var raw = OpenRaw(day);
        if (raw == null) return;
        var lines = Summarize(raw);
        try { File.WriteAllLines(MinutePath(day), lines); }
        catch (UnauthorizedAccessException) { } // the non-elevated MCP server can't write here — it just summarizes again next time
    }

    /// <summary>Raw lines → one S row per minute (peaks), its top apps as P rows, and the E rows as they are.</summary>
    private static List<string> Summarize(StreamReader raw)
    {
        var minutes = new SortedDictionary<long, float[]>(); // the minute's peak of each S column after the time
        // Peaks, not averages, for apps too: a 15-second burst to 100% must still read as 100% at minute level.
        var procs = new Dictionary<long, Dictionary<string, (float Cpu, float Ram, float Gpu)>>();
        var events = new List<string>();

        string? line;
        while ((line = raw.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length < 3 || !long.TryParse(f[1], Inv, out long t)) continue;
            long m = t / 60 * 60;
            if (f[0] == "S" && f.Length >= 7)
            {
                if (!minutes.TryGetValue(m, out var peaks)) { peaks = new float[SysColumns]; Array.Fill(peaks, float.NaN); minutes[m] = peaks; }
                for (int i = 0; i < SysColumns && i + 2 < f.Length; i++)
                {
                    float v = P(f[i + 2]);
                    // A battery's minute is its lowest charge, not its highest: the drain is what matters.
                    peaks[i] = i == BatteryColumn ? Min(peaks[i], v) : Max(peaks[i], v);
                }
            }
            else if (f[0] == "P" && f.Length >= 6)
            {
                if (!procs.TryGetValue(m, out var byName)) procs[m] = byName = new();
                var cur = byName.GetValueOrDefault(f[2]);
                byName[f[2]] = (Math.Max(cur.Cpu, Z(P(f[3]))), Math.Max(cur.Ram, Z(P(f[4]))), Math.Max(cur.Gpu, Z(P(f[5]))));
            }
            else if (f[0] == "E") events.Add(line);
        }

        var lines = new List<string>(minutes.Count * 8 + events.Count);
        foreach (var (m, s) in minutes)
            lines.Add($"S,{m}," + string.Join(',', s.Select(F)));
        foreach (var (m, byName) in procs)
        {
            var rows = byName.Select(kv => (Name: kv.Key, kv.Value.Cpu, kv.Value.Ram, kv.Value.Gpu)).ToList();
            foreach (var p in rows.OrderByDescending(p => p.Cpu).Take(5)
                         .Concat(rows.OrderByDescending(p => p.Ram).Take(3))
                         .Concat(rows.OrderByDescending(p => p.Gpu).Take(3).Where(p => p.Gpu >= 0.5))
                         .DistinctBy(p => p.Name))
                lines.Add($"P,{m},{p.Name},{F(p.Cpu)},{F(p.Ram)},{F(p.Gpu)}");
        }
        lines.AddRange(events);
        return lines;
    }

    private const int SysColumns = 11, BatteryColumn = 10; // S values after the time: cpu … battery
    private static float Max(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b);
    private static float Min(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Min(a, b);

    /// <summary>History lines from anywhere (another PC's, say), sorted the way readers expect.</summary>
    public static HistoryData Parse(TextReader reader)
    {
        var data = new HistoryData(new(), new(), new());
        Parse(reader, long.MinValue, long.MaxValue, data, systemOnly: false);
        return data;
    }

    /// <summary>A day's lines as stored — raw, or the per-minute summary — or null if nothing was recorded.</summary>
    public static StreamReader? OpenDay(DateTime day, bool minutes) => minutes ? OpenMinutes(day) : OpenRaw(day);

    private static void Parse(TextReader reader, long fromT, long toT, HistoryData data, bool systemOnly)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length < 3 || !long.TryParse(f[1], Inv, out long t) || t < fromT || t > toT) continue;
            var time = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
            if (f[0] == "S" && f.Length >= 7)
            {
                float C(int i) => f.Length > i ? P(f[i]) : float.NaN; // older files have fewer columns
                data.System.Add(new SysSample(time, P(f[2]), P(f[3]), P(f[4]), P(f[5]), P(f[6]), C(7), C(8), C(9), C(10), C(11), C(12)));
            }
            else if (systemOnly) continue;
            else if (f[0] == "P" && f.Length >= 6)
                data.Processes.Add(new ProcSample(time, f[2], P(f[3]), P(f[4]), P(f[5])));
            else if (f[0] == "E")
                data.Events.Add(new HistoryEvent(time, f[2], f.Length > 3 ? f[3] : ""));
        }
        // Minute summaries list S rows, then P, then E, and a launch is written ~10 s after the time it
        // carries — callers need each list in time order.
        data.Processes.Sort((a, b) => a.Time.CompareTo(b.Time));
        data.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static float P(string s) => float.TryParse(s, NumberStyles.Float, Inv, out float v) ? v : float.NaN;

    private static FileStream Shared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static StreamReader? OpenRaw(DateTime day)
    {
        string csv = RawPath(day);
        if (File.Exists(csv + ".gz")) return new StreamReader(new GZipStream(Shared(csv + ".gz"), CompressionMode.Decompress));
        if (File.Exists(csv)) return new StreamReader(Shared(csv));
        return null;
    }

    /// <summary>The oldest day with anything recorded, for the navigator's "All".</summary>
    public static DateTime? OldestDay() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder).Select(f => Path.GetFileName(f))
                .Select(n => DateTime.TryParseExact(n[..Math.Min(10, n.Length)], "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : (DateTime?)null)
                .Where(d => d != null).Min()
            : null;

    /// <summary>Disk used by the history, in MB.</summary>
    public static double SizeMB() => Directory.Exists(Folder) ? Directory.GetFiles(Folder).Sum(f => new FileInfo(f).Length) / 1048576.0 : 0;

    /// <summary>
    /// Who used what across a set of process snapshots. Only the top processes are recorded each time,
    /// so a process absent from a snapshot counts as 0 there — averages are over every snapshot in range.
    /// </summary>
    public static List<ProcessTotal> TopProcesses(IReadOnlyCollection<ProcSample> samples, string sortBy = "cpu", int limit = 10)
    {
        int snapshots = Math.Max(1, samples.Select(s => s.Time).Distinct().Count());
        var totals = samples.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => new ProcessTotal(
            g.Key,
            g.Sum(s => Z(s.Cpu)) / snapshots, g.Max(s => Z(s.Cpu)),
            g.Sum(s => Z(s.RamMB)) / snapshots, g.Max(s => Z(s.RamMB)),
            g.Sum(s => Z(s.Gpu)) / snapshots, g.Max(s => Z(s.Gpu)),
            g.Count()));
        totals = sortBy switch
        {
            "ram" => totals.OrderByDescending(t => t.AvgRamMB),
            "gpu" => totals.OrderByDescending(t => t.AvgGpu),
            _ => totals.OrderByDescending(t => t.AvgCpu),
        };
        return totals.Take(limit).ToList();
    }

    private static float Z(float v) => float.IsNaN(v) ? 0 : v;
}
