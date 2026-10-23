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
    float DiskMBs = float.NaN, float NetMbps = float.NaN, float CpuWatts = float.NaN, float GpuWatts = float.NaN, float FanRpm = float.NaN, float BatteryPct = float.NaN,
    float Fps = float.NaN);
/// <param name="Instances">How many processes of the exe ran (chrome × 30 → 30); 0 in files from before 1.17.</param>
public readonly record struct ProcSample(DateTime Time, string Name, float Cpu, float RamMB, float Gpu, float Fps = float.NaN, int Instances = 0);
/// <param name="Kind">launch (Detail = app), sleep, wake, start (Detail = boot time, unix seconds).</param>
public readonly record struct HistoryEvent(DateTime Time, string Kind, string Detail);
public sealed record ProcessTotal(string Name, double AvgCpu, double PeakCpu, double AvgRamMB, double PeakRamMB, double AvgGpu, double PeakGpu, int Seen);
public sealed record HistoryData(List<SysSample> System, List<ProcSample> Processes, List<HistoryEvent> Events);
/// <summary>A stretch of the app index: how many app snapshots were taken in it, and the whole PC's averages over it.</summary>
public readonly record struct AppSlot(DateTime Time, int Snapshots, float Cpu, float Mem, float Gpu);
/// <summary>One app in one slot: in how many of its snapshots it was recorded, its averages over those and its peaks.</summary>
public readonly record struct AppPoint(DateTime Time, string Name, int Seen, float Cpu, float PeakCpu, float RamMB, float PeakRamMB, float Gpu, float PeakGpu, int Instances);
public sealed record AppHistory(int SlotSeconds, List<AppSlot> Slots, List<AppPoint> Apps);
/// <param name="Slots">How many slots it was recorded in.</param>
public sealed record AppTotal(string Name, double Cpu, float PeakCpu, double RamMB, float PeakRamMB, double Gpu, float PeakGpu, int Slots, int Processes);
public sealed record AppSession(DateTime Start, DateTime End, double Cpu, float PeakCpu, double RamMB, float PeakRamMB, double Gpu, float PeakGpu, int Processes);

/// <summary>
/// The recorded history: one plain-text file per local day in DataFolder (writable by the elevated
/// app, readable by the non-elevated MCP server). Lines:
///   S,unixSeconds,cpu,mem,gpu,cpuTemp,gpuTemp,diskMBs,netMbps,cpuW,gpuW,fanRpm,battery%,fps (the app in front)
///                                                 — every second (blank when unknown; older files end
///                                                   earlier: at gpuTemp before 1.5, at netMbps before 1.6)
///   P,unixSeconds,name,cpu,ramMB,gpu,fps,count    — the apps, every 10 seconds (fps from 1.10; the process
///                                                   count, and every app over a small threshold, from 1.17)
///   E,unixSeconds,kind,detail                     — app launches, sleep/wake, app start (with boot time)
/// Today's file is appended and flushed line by line, so a hard reset loses at most a torn last line,
/// which the reader skips. A finished day gets a per-minute summary beside it in the same format (S = the
/// minute's peaks, P = the minute's top apps), so weeks and months load in a blink, and both are packed with
/// Brotli at its smallest (~6x; days gzipped before 1.8 are repacked, and still read meanwhile).
/// Shared verbatim with Sysoptimizer.Mcp — keep it free of WPF.
/// </summary>
public static class HistoryStore
{
    public const int MaxRetentionDays = 366;
    /// <summary>Set by the app from the user's choice; days older than this are deleted at each day change.</summary>
    public static int RetentionDays { get; set; } = 90;
    /// <summary>All of the app's data (history, log, the lists ChangeService keeps), the studio's folder since 1.20. The app runs
    /// elevated as the signed-in user, so this is that user's Roaming folder, the same one the non-elevated MCP server sees.</summary>
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs", "Sysoptimizer");
    /// <summary>Where everything was kept before 1.20.</summary>
    public static readonly string OldDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sysoptimizer");
    /// <summary>Settable for the self-test, which works in a temp folder. The old folder while the move hasn't happened yet:
    /// the MCP server may read before the app has started once since the update.</summary>
    public static string Folder { get; set; } = Path.Combine(Directory.Exists(OldDataFolder) && !Directory.Exists(DataFolder) ? OldDataFolder : DataFolder, "history");

    /// <summary>
    /// Moves the data folder from where it was to where it goes, once (the installer does the same while the app is stopped;
    /// this covers a silent update and an install made from another admin account). Returns the folder to use now and, in
    /// <paramref name="note"/>, a line for the log. The same volume, so a rename: instant at any size, never half done.
    /// When it fails (a file held open) the old folder is used this time and the move is tried at the next start. A new
    /// folder that already has data is never mixed with the old one.
    /// </summary>
    public static string MoveData(string from, string to, out string? note)
    {
        note = null;
        if (!Directory.Exists(from)) return to;
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            note = $"Data left in {from}: {to} already has data";
            return to;
        }
        try
        {
            if (Directory.Exists(to)) Directory.Delete(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = $"Couldn't move the data to {to} ({ex.Message}); using {from} until the next start";
            return from;
        }
        // A rename keeps ProgramData's permissions, where the user could read but not delete their own files: take the new parent's.
        using (var icacls = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", $"\"{to}\" /reset /T /C /Q") { CreateNoWindow = true, UseShellExecute = false }))
            icacls?.WaitForExit(60_000);
        note = $"Data moved from {from} to {to}";
        return to;
    }
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
                + $"{F(sample.CpuWatts)},{F(sample.GpuWatts)},{F(sample.FanRpm)},{F(sample.BatteryPct)},{F(sample.Fps)}");
            if (processes == null) return;
            foreach (var p in processes)
                w.WriteLine($"P,{t},{Clean(p.Name)},{F(p.Cpu)},{F(p.RamMB)},{F(p.Gpu)},{F(p.Fps)},{p.Instances}");
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
        // Packing takes seconds per day: never on the recording path (Write runs on the UI thread).
        Task.Run(() => Maintain(day));
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
    private static string AppIndexPath(DateTime day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.apps.csv");

    private static readonly object MaintainGate = new();

    /// <summary>Summarizes and packs every finished day (and repacks gzipped ones); deletes days past retention.</summary>
    public static void Maintain(DateTime today)
    {
        lock (MaintainGate) // a second run (the day changed again, or the self-test) waits, then finds little left
        {
            foreach (var file in Directory.GetFiles(Folder))
            {
                string name = Path.GetFileName(file);
                if (!DateTime.TryParseExact(name[..Math.Min(10, name.Length)], "yyyy-MM-dd", Inv, DateTimeStyles.None, out var day)) continue;
                try
                {
                    if (day < today.AddDays(-RetentionDays)) { File.Delete(file); continue; }
                    if (day >= today) continue;
                    if (name == $"{day:yyyy-MM-dd}.csv") EnsureMinuteSummary(day);
                    // Days recorded before 1.17 get their app index here too, once: it's only added, nothing is rewritten.
                    if (name == $"{day:yyyy-MM-dd}.csv" || name == $"{day:yyyy-MM-dd}.csv.br" || name == $"{day:yyyy-MM-dd}.csv.gz") EnsureAppIndex(day);
                    if (name.EndsWith(".csv")) Pack(file, File.OpenRead(file), file + ".br");
                    else if (name.EndsWith(".csv.gz")) Pack(file, new GZipStream(File.OpenRead(file), CompressionMode.Decompress), file[..^3] + ".br");
                }
                catch (IOException) { } // a reader has it open right now; try again on the next day change
            }
            foreach (var file in Directory.GetFiles(Folder, "*.csv").Where(f => f.EndsWith(".min.csv") || f.EndsWith(".apps.csv"))) // summaries written since the loop listed the folder
                try { Pack(file, File.OpenRead(file), file + ".br"); }
                catch (IOException) { }
        }
    }

    /// <summary>Writes the packed copy under a temporary name first, so a reader never finds half a file.</summary>
    private static void Pack(string file, Stream source, string target)
    {
        using (source)
        using (var packed = new BrotliStream(File.Create(target + ".tmp"), CompressionLevel.SmallestSize))
            source.CopyTo(packed);
        File.Move(target + ".tmp", target, overwrite: true);
        File.Delete(file);
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
        if (!minutes && day == DateTime.Today && ReadTodayTail() is { } today) return today;
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
            string path = MinutePath(day);
            return File.Exists(path + ".br") ? new StreamReader(new BrotliStream(Shared(path + ".br"), CompressionMode.Decompress))
                 : File.Exists(path) ? new StreamReader(Shared(path)) : null;
        }
        using var raw = OpenRaw(day);
        return raw == null ? null : new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', Summarize(raw)))));
    }

    private static void EnsureMinuteSummary(DateTime day)
    {
        if (File.Exists(MinutePath(day)) || File.Exists(MinutePath(day) + ".br")) return;
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
        var procs = new Dictionary<long, Dictionary<string, (float Cpu, float Ram, float Gpu, float Fps)>>();
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
                    // A battery's minute is its lowest charge, not its highest: the drain is what matters. FPS
                    // too: a minute's worst frame rate is the stutter worth seeing, not its best.
                    peaks[i] = i is BatteryColumn or FpsColumn ? Min(peaks[i], v) : Max(peaks[i], v);
                }
            }
            else if (f[0] == "P" && f.Length >= 6)
            {
                if (!procs.TryGetValue(m, out var byName)) procs[m] = byName = new();
                var cur = byName.TryGetValue(f[2], out var seen) ? seen : (0, 0, 0, float.NaN);
                byName[f[2]] = (Math.Max(cur.Cpu, Z(P(f[3]))), Math.Max(cur.Ram, Z(P(f[4]))), Math.Max(cur.Gpu, Z(P(f[5]))), Min(cur.Fps, f.Length > 6 ? P(f[6]) : float.NaN));
            }
            else if (f[0] == "E") events.Add(line);
        }

        var lines = new List<string>(minutes.Count * 8 + events.Count);
        foreach (var (m, s) in minutes)
            lines.Add($"S,{m}," + string.Join(',', s.Select(F)));
        foreach (var (m, byName) in procs)
        {
            var rows = byName.Select(kv => (Name: kv.Key, kv.Value.Cpu, kv.Value.Ram, kv.Value.Gpu, kv.Value.Fps)).ToList();
            foreach (var p in rows.OrderByDescending(p => p.Cpu).Take(5)
                         .Concat(rows.OrderByDescending(p => p.Ram).Take(3))
                         .Concat(rows.OrderByDescending(p => p.Gpu).Take(3).Where(p => p.Gpu >= 0.5))
                         .Concat(rows.Where(p => p.Fps >= 1).OrderByDescending(p => p.Fps).Take(3))
                         .DistinctBy(p => p.Name))
                lines.Add($"P,{m},{p.Name},{F(p.Cpu)},{F(p.Ram)},{F(p.Gpu)},{F(p.Fps)}");
        }
        lines.AddRange(events);
        return lines;
    }

    // --- the app index: each app's use per 5 minutes, so an app's month loads in a blink ---

    /// <summary>The app index's slot: 5 minutes — a 30-day chart has ~3 h per column, a day ~6 min.</summary>
    public const int AppSlotSeconds = 300;

    /// <summary>
    /// Every app recorded in [from, to], slot by slot, with the PC's own averages per slot. Up to 3 hours it
    /// comes from the snapshots themselves in 30-second slots; longer, from the per-day app index (today's
    /// summarized on the fly). A slot without an app's row means it wasn't in that slot's snapshots — closed,
    /// or under the recording threshold — not that it used nothing; a missing slot means nothing was recorded.
    /// </summary>
    public static AppHistory ReadApps(DateTime from, DateTime to)
    {
        int slot = (to - from).TotalHours <= 3 ? 30 : AppSlotSeconds;
        var result = new AppHistory(slot, new(), new());
        long fromT = Unix(from) / slot * slot, toT = Unix(to);
        var today = DateTime.Now.Date;
        for (var day = from.ToLocalTime().Date; day <= to.ToLocalTime().Date; day = day.AddDays(1))
        {
            using var reader = slot == AppSlotSeconds && day < today ? OpenAppIndex(day) : SummarizeApps(OpenRaw(day), slot);
            if (reader != null) ParseApps(reader, fromT, toT, result);
        }
        result.Slots.Sort((a, b) => a.Time.CompareTo(b.Time));
        result.Apps.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private static TextReader? OpenAppIndex(DateTime day)
    {
        EnsureAppIndex(day);
        string path = AppIndexPath(day);
        if (File.Exists(path + ".br")) return new StreamReader(new BrotliStream(Shared(path + ".br"), CompressionMode.Decompress));
        if (File.Exists(path)) return new StreamReader(Shared(path));
        return SummarizeApps(OpenRaw(day), AppSlotSeconds); // couldn't write it (the non-elevated MCP server): summarized in memory
    }

    /// <summary>Builds a finished day's app index from its raw file, written under a temporary name first. Never touches the raw file.</summary>
    private static void EnsureAppIndex(DateTime day)
    {
        string path = AppIndexPath(day);
        if (File.Exists(path) || File.Exists(path + ".br")) return;
        using var index = SummarizeApps(OpenRaw(day), AppSlotSeconds);
        if (index == null) return;
        try
        {
            File.WriteAllText(path + ".tmp", index.ReadToEnd());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (UnauthorizedAccessException) { } // the non-elevated MCP server can't write here
    }

    /// <summary>
    /// Raw lines → per slot: T,slot,snapshots,cpu,mem,gpu (the PC's averages over its S rows) and, per app recorded in it,
    /// A,slot,name,seen,cpu,peakCpu,ramMB,peakRamMB,gpu,peakGpu,processes (averages over the snapshots it was in; peaks).
    /// </summary>
    private static StringReader? SummarizeApps(StreamReader? raw, int slot)
    {
        if (raw == null) return null;
        using (raw)
        {
            var sys = new SortedDictionary<long, (int N, double Cpu, double Mem, double Gpu, int GpuN)>();
            var snapshots = new Dictionary<long, HashSet<long>>();
            var apps = new Dictionary<(long Slot, string Name), (int Seen, double Cpu, float PeakCpu, double Ram, float PeakRam, double Gpu, float PeakGpu, int Count)>();
            string? line;
            while ((line = raw.ReadLine()) != null)
            {
                var f = line.Split(',');
                if (f.Length < 6 || !long.TryParse(f[1], Inv, out long t)) continue;
                long k = t / slot * slot;
                if (f[0] == "S")
                {
                    var c = sys.GetValueOrDefault(k);
                    float gpu = P(f[4]);
                    sys[k] = (c.N + 1, c.Cpu + Z(P(f[2])), c.Mem + Z(P(f[3])), c.Gpu + Z(gpu), c.GpuN + (float.IsNaN(gpu) ? 0 : 1));
                }
                else if (f[0] == "P")
                {
                    if (!snapshots.TryGetValue(k, out var times)) snapshots[k] = times = new();
                    times.Add(t);
                    float cpu = Z(P(f[3])), ram = Z(P(f[4])), g = Z(P(f[5]));
                    int count = f.Length > 7 && int.TryParse(f[7], out int n) ? n : 0;
                    var a = apps.GetValueOrDefault((k, f[2]));
                    apps[(k, f[2])] = (a.Seen + 1, a.Cpu + cpu, Math.Max(a.PeakCpu, cpu), a.Ram + ram, Math.Max(a.PeakRam, ram), a.Gpu + g, Math.Max(a.PeakGpu, g), Math.Max(a.Count, count));
                }
            }
            var text = new System.Text.StringBuilder();
            foreach (var (k, c) in sys)
                text.Append($"T,{k},{snapshots.GetValueOrDefault(k)?.Count ?? 0},{F((float)(c.Cpu / c.N))},{F((float)(c.Mem / c.N))},{(c.GpuN > 0 ? F((float)(c.Gpu / c.GpuN)) : "")}").Append('\n');
            foreach (var ((k, name), a) in apps)
                text.Append($"A,{k},{name},{a.Seen},{F((float)(a.Cpu / a.Seen))},{F(a.PeakCpu)},{F((float)(a.Ram / a.Seen))},{F(a.PeakRam)},{F((float)(a.Gpu / a.Seen))},{F(a.PeakGpu)},{a.Count}").Append('\n');
            return new StringReader(text.ToString());
        }
    }

    private static void ParseApps(TextReader reader, long fromT, long toT, AppHistory into)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length < 6 || !long.TryParse(f[1], Inv, out long t) || t < fromT || t > toT) continue;
            var time = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
            if (f[0] == "T")
                into.Slots.Add(new AppSlot(time, int.TryParse(f[2], out int n) ? n : 0, P(f[3]), P(f[4]), P(f[5])));
            else if (f[0] == "A" && f.Length >= 11)
                into.Apps.Add(new AppPoint(time, string.Intern(f[2]), int.TryParse(f[3], out int seen) ? seen : 1, P(f[4]), P(f[5]), P(f[6]), P(f[7]), P(f[8]), P(f[9]),
                    int.TryParse(f[10], out int count) ? count : 0));
        }
    }

    /// <summary>
    /// Each app in the range: averages over the snapshots it was recorded in (a snapshot without it isn't
    /// counted as 0 — it wasn't measured), its peaks, how many slots it was seen in, and its most processes.
    /// </summary>
    public static List<AppTotal> AppTotals(AppHistory h) =>
        h.Apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            double seen = Math.Max(1, g.Sum(a => a.Seen));
            return new AppTotal(g.Key, g.Sum(a => a.Cpu * a.Seen) / seen, g.Max(a => a.PeakCpu), g.Sum(a => a.RamMB * a.Seen) / seen, g.Max(a => a.PeakRamMB),
                g.Sum(a => a.Gpu * a.Seen) / seen, g.Max(a => a.PeakGpu), g.Count(), g.Max(a => a.Instances));
        }).ToList();

    /// <summary>
    /// An app's sessions: its slots in a row. A slot or two without it (closed for a moment, or under the
    /// recording threshold) doesn't end one — longer does, and so does any stretch with nothing recorded at all.
    /// </summary>
    public static List<AppSession> AppSessions(AppHistory h, string name)
    {
        var mine = h.Apps.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).DistinctBy(a => a.Time).ToDictionary(a => a.Time);
        var sessions = new List<AppSession>();
        var run = new List<AppPoint>();
        int allowed = h.SlotSeconds >= AppSlotSeconds ? 1 : 2, missed = 0;
        DateTime? previous = null;
        void Close()
        {
            if (run.Count > 0)
            {
                double seen = run.Sum(a => a.Seen);
                sessions.Add(new AppSession(run[0].Time, run[^1].Time.AddSeconds(h.SlotSeconds),
                    run.Sum(a => a.Cpu * a.Seen) / seen, run.Max(a => a.PeakCpu), run.Sum(a => a.RamMB * a.Seen) / seen, run.Max(a => a.PeakRamMB),
                    run.Sum(a => a.Gpu * a.Seen) / seen, run.Max(a => a.PeakGpu), run.Max(a => a.Instances)));
            }
            run.Clear();
            missed = 0;
        }
        foreach (var slot in h.Slots)
        {
            if (previous is DateTime p && (slot.Time - p).TotalSeconds > h.SlotSeconds * 1.5) Close(); // nothing recorded in between
            previous = slot.Time;
            if (mine.TryGetValue(slot.Time, out var point)) { run.Add(point); missed = 0; }
            else if (run.Count > 0 && ++missed > allowed) Close();
        }
        Close();
        return sessions;
    }

    /// <summary>
    /// One row per slot of the range for one app: recorded (its averages and peaks, the process count), "not recorded"
    /// (snapshots were taken, it wasn't in them) or "nothing recorded" (no snapshots at all) — never a made-up 0.
    /// Invariant numbers and commas, so a script reads it the same on any PC. The PC's own use sits beside it.
    /// </summary>
    public static string AppCsv(AppHistory h, string app)
    {
        string N(float v) => float.IsNaN(v) ? "" : v.ToString("0.##", Inv);
        var mine = h.Apps.Where(a => string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase)).DistinctBy(a => a.Time).ToDictionary(a => a.Time);
        var slots = h.Slots.DistinctBy(s => s.Time).ToDictionary(s => s.Time);
        var text = new System.Text.StringBuilder("time,status,cpu_pct,cpu_peak_pct,memory_mb,memory_peak_mb,gpu_pct,gpu_peak_pct,processes,pc_cpu_pct,pc_memory_pct,pc_gpu_pct\n");
        if (h.Slots.Count == 0) return text.ToString();
        var now = DateTime.UtcNow;
        for (var t = h.Slots[0].Time; t <= h.Slots[^1].Time && t <= now; t = t.AddSeconds(h.SlotSeconds))
        {
            string when = t.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", Inv);
            if (!slots.TryGetValue(t, out var s) || s.Snapshots == 0) { text.Append($"{when},nothing recorded,,,,,,,,,,\n"); continue; }
            string pc = $"{N(s.Cpu)},{N(s.Mem)},{N(s.Gpu)}";
            text.Append(mine.TryGetValue(t, out var a)
                ? $"{when},recorded,{N(a.Cpu)},{N(a.PeakCpu)},{N(a.RamMB)},{N(a.PeakRamMB)},{N(a.Gpu)},{N(a.PeakGpu)},{(a.Instances > 0 ? a.Instances.ToString(Inv) : "")},{pc}\n"
                : $"{when},not recorded,,,,,,,,{pc}\n");
        }
        return text.ToString();
    }

    private const int SysColumns = 12, BatteryColumn = 10, FpsColumn = 11; // S values after the time: cpu … battery, fps
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
                data.System.Add(new SysSample(time, P(f[2]), P(f[3]), P(f[4]), P(f[5]), P(f[6]), C(7), C(8), C(9), C(10), C(11), C(12), C(13)));
            }
            else if (systemOnly) continue;
            else if (f[0] == "P" && f.Length >= 6)
                data.Processes.Add(new ProcSample(time, string.Intern(f[2]), P(f[3]), P(f[4]), P(f[5]), f.Length > 6 ? P(f[6]) : float.NaN, f.Length > 7 && int.TryParse(f[7], out int n) ? n : 0));
            else if (f[0] == "E")
                data.Events.Add(new HistoryEvent(time, string.Intern(f[2]), f.Length > 3 ? string.Intern(f[3]) : ""));
        }
        // Minute summaries list S rows, then P, then E, and a launch is written ~10 s after the time it
        // carries — callers need each list in time order.
        data.Processes.Sort((a, b) => a.Time.CompareTo(b.Time));
        data.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // Today's file, parsed once and then only what was appended since: the History tab re-reads today every
    // ten seconds, and parsing the whole day again each time cost a quarter of a second and ~50 MB of garbage.
    private static readonly object TodayGate = new();
    private static DateTime _todayDay;
    private static long _todayOffset;
    private static HistoryData _todayData = new(new(), new(), new());

    /// <summary>Today's per-second data, read incrementally; null when today's file isn't plain CSV (yet).</summary>
    private static HistoryData? ReadTodayTail()
    {
        string csv = RawPath(DateTime.Today);
        lock (TodayGate)
        {
            if (!File.Exists(csv)) return null;
            using var stream = Shared(csv);
            if (_todayDay != DateTime.Today || stream.Length < _todayOffset) (_todayDay, _todayOffset, _todayData) = (DateTime.Today, 0, new(new(), new(), new()));
            stream.Seek(_todayOffset, SeekOrigin.Begin);
            var bytes = new byte[stream.Length - _todayOffset];
            stream.ReadExactly(bytes);
            int end = Array.LastIndexOf(bytes, (byte)'\n') + 1; // a line still being written waits for the next read
            var fresh = new HistoryData(new(), new(), new());
            Parse(new StringReader(System.Text.Encoding.UTF8.GetString(bytes, 0, end)), long.MinValue, long.MaxValue, fresh, systemOnly: false);
            _todayOffset += end;
            Append(_todayData, fresh);
            return Copy(_todayData);
        }
    }

    /// <summary>Drops today's parsed rows; the next read parses the file afresh.</summary>
    public static void ForgetToday()
    {
        lock (TodayGate) (_todayDay, _todayOffset, _todayData) = (default, 0, new(new(), new(), new()));
    }

    /// <summary>Adds newer rows to a day, keeping each list in time order (a launch is written after the time it carries).</summary>
    public static void Append(HistoryData day, HistoryData fresh)
    {
        static void Add<T>(List<T> to, List<T> more, Func<T, DateTime> time)
        {
            if (more.Count == 0) return;
            bool sorted = to.Count == 0 || time(more[0]) >= time(to[^1]);
            to.AddRange(more);
            if (!sorted) to.Sort((a, b) => time(a).CompareTo(time(b)));
        }
        Add(day.System, fresh.System, s => s.Time);
        Add(day.Processes, fresh.Processes, p => p.Time);
        Add(day.Events, fresh.Events, e => e.Time);
    }

    /// <summary>A copy the caller can keep while the original grows on another thread.</summary>
    public static HistoryData Copy(HistoryData d) => new(new(d.System), new(d.Processes), new(d.Events));

    private static float P(string s) => float.TryParse(s, NumberStyles.Float, Inv, out float v) ? v : float.NaN;

    private static FileStream Shared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static StreamReader? OpenRaw(DateTime day)
    {
        string csv = RawPath(day);
        if (File.Exists(csv + ".br")) return new StreamReader(new BrotliStream(Shared(csv + ".br"), CompressionMode.Decompress));
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
