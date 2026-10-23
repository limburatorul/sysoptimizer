using System.IO.Compression;
using System.Text.Json;
using Sysoptimizer.Services;

// Exercises HistoryStore in a temp folder: writing and reading back, files from before disk/network were
// recorded, torn lines, minute summaries keeping short spikes, gzip + retention, and per-app averages.

// "serve <port> <key>": this PC's real history on localhost only (no firewall rule, no admin), for trying
// the app's "Watch another PC" against it.
if (args is ["serve", var servePort, var serveKey])
{
    using var server = new HistoryServer(int.Parse(servePort), RemoteCrypto.ParseKey(serveKey) ?? throw new ArgumentException("not a key"), host: "localhost");
    Console.WriteLine($"Serving {HistoryStore.Folder} on localhost:{servePort} — Ctrl+C to stop.");
    Thread.Sleep(Timeout.Infinite);
}

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok) failures++;
}
static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

// A game on a mapped network drive is W:\… in Shelf and \\server\share\… to the elevated app: the same once rootless.
Check(Ecosystem.NoRoot(@"\\10.10.0.253\Games\SteamUnlocked\X\x.exe") == Ecosystem.NoRoot(@"W:\SteamUnlocked\X\x.exe")
      && Ecosystem.NoRoot(@"\\?\UNC\nas\Games\a.exe") == @"\a.exe" && Ecosystem.NoRoot(@"\\?\C:\a.exe") == @"\a.exe"
      && Ecosystem.NoRoot(@"C:\Games\a.exe") == @"\Games\a.exe", "fps: a game's path matches whatever drive or share it is reached by");

foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "sysoptimizer-selftest-*")) // earlier runs can't delete their own (today's file is open)
    try { Directory.Delete(old, recursive: true); } catch (IOException) { } // another run still has it
string folder = Path.Combine(Path.GetTempPath(), "sysoptimizer-selftest-" + Guid.NewGuid().ToString("N")); // never a leftover from an earlier run
Directory.CreateDirectory(folder);
HistoryStore.Folder = folder;
AppLog.File = Path.Combine(folder, "test.log");
HistoryStore.RetentionDays = 30;

try
{
    // 1. Today: write a sample, its apps and an event, read them back at full resolution.
    var now = DateTime.UtcNow;
    HistoryStore.Write(new SysSample(now, 12.5f, 40, 7, 65, 45, 33.3f, 8.5f, 107.5f, 53, 1164, float.NaN),
        new[] { new ProcSample(now, "chrome", 5.5f, 1200, 2), new ProcSample(now, "game,with comma", 1, 300, 50) });
    HistoryStore.WriteEvent("launch", "notepad", now);
    var today = HistoryStore.Read(now.AddMinutes(-1), now.AddMinutes(1));
    var s = today.System.SingleOrDefault();
    Check(today.System.Count == 1, "today: one sample read back");
    Check(s.Cpu == 12.5f && s.Mem == 40 && s.Gpu == 7 && s.CpuTemp == 65 && s.GpuTemp == 45, "today: cpu/mem/gpu/temps round-trip");
    Check(Math.Abs(s.DiskMBs - 33.3f) < 0.01 && s.NetMbps == 8.5f, "today: disk and network round-trip");
    Check(s.CpuWatts == 107.5f && s.GpuWatts == 53 && s.FanRpm == 1164 && float.IsNaN(s.BatteryPct), "today: power, fan and (no) battery round-trip");
    Check(today.Processes.Count == 2 && today.Processes.Any(p => p.Name == "game_with comma" && p.Gpu == 50), "today: apps round-trip, a comma in a name can't split the line");
    Check(today.Events.Count == 1 && today.Events[0].Kind == "launch" && today.Events[0].Detail == "notepad", "today: event round-trip");

    // Today is read incrementally: only what was appended, never a half-written line, the earlier result untouched.
    var day1 = HistoryStore.ReadDay(DateTime.Today, minutes: false);
    void AppendRaw(string text)
    {
        using var f = new FileStream(Path.Combine(folder, $"{DateTime.Today:yyyy-MM-dd}.csv"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        f.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }
    long tailT = Unix(now);
    AppendRaw($"S,{tailT + 1},20,40,7,65,45\nS,{tailT + 2},2");
    var day2 = HistoryStore.ReadDay(DateTime.Today, minutes: false);
    AppendRaw($"1,40,7,65,45\nE,{tailT - 5},launch,late\n");
    var day3 = HistoryStore.ReadDay(DateTime.Today, minutes: false);
    Check(day1.System.Count == 1 && day2.System.Count == 2 && day3.System.Count == 3 && day3.System[^1].Cpu == 21,
        "today: appended lines read once, a half-written line waits for its end");
    Check(day3.Events.Select(e => e.Detail).SequenceEqual(new[] { "late", "notepad" }), "today: a late-stamped line lands in time order");

    // The log file: lines kept in order, a message's own line breaks indented under it, the tail in whole lines.
    AppLog.Write("first");
    AppLog.Write("Crash: boom\n   at Somewhere()");
    var log = AppLog.Tail();
    Check(log.Contains(" first\n") && log.Contains(" Crash: boom\n       at Somewhere()\n") && log.IndexOf("first") < log.IndexOf("Crash"), "log: written in order, a stack trace kept under its line");
    var tail = AppLog.Tail(30);
    Check(tail.Length <= 30 && tail.Length > 0 && log.Contains("\n" + tail), "log: the tail is whole lines from the end");

    // 1.20: the data folder moves from ProgramData to %APPDATA%\ProtagonistLabs\Sysoptimizer, here between synthetic folders.
    string moves = Path.Combine(folder, "moves");
    string Old(string name) { var d = Path.Combine(moves, name, "ProgramData", "Sysoptimizer"); Directory.CreateDirectory(Path.Combine(d, "history")); File.WriteAllText(Path.Combine(d, "history", "x.csv"), name); return d; }
    string New(string name) => Path.Combine(moves, name, "Roaming", "ProtagonistLabs", "Sysoptimizer");
    string from = Old("fresh"), to = New("fresh");
    Check(HistoryStore.MoveData(from, to, out var note) == to && !Directory.Exists(from) && File.ReadAllText(Path.Combine(to, "history", "x.csv")) == "fresh" && note!.StartsWith("Data moved"),
          "move: the old folder becomes the new one, whole");
    Check(HistoryStore.MoveData(from, to, out note) == to && note == null, "move: done once, nothing to do the next time");
    from = Old("empty"); to = New("empty"); Directory.CreateDirectory(to);
    Check(HistoryStore.MoveData(from, to, out _) == to && !Directory.Exists(from) && File.Exists(Path.Combine(to, "history", "x.csv")), "move: an empty new folder is taken over");
    from = Old("both"); to = New("both"); Directory.CreateDirectory(to); File.WriteAllText(Path.Combine(to, "keep.txt"), "new");
    Check(HistoryStore.MoveData(from, to, out note) == to && File.Exists(Path.Combine(from, "history", "x.csv")) && !Directory.Exists(Path.Combine(to, "history")) && note!.StartsWith("Data left"),
          "move: a new folder with data is never mixed with the old one");
    from = Old("locked"); to = New("locked");
    using (new FileStream(Path.Combine(from, "history", "x.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        Check(HistoryStore.MoveData(from, to, out note) == from && File.Exists(Path.Combine(from, "history", "x.csv")) && note!.StartsWith("Couldn't move"),
              "move: a file held open keeps the old folder in use, nothing half moved");
    Check(HistoryStore.MoveData(from, to, out _) == to && File.Exists(Path.Combine(to, "history", "x.csv")), "move: and it goes at the next start");

    // What changed on the PC: apps that appeared, went or changed version; the list kept between runs.
    ChangeService.Folder = folder;
    Check(ChangeService.Load("apps-test.txt") == null, "changes: the first look finds no list from before");
    ChangeService.Save("apps-test.txt", new Dictionary<string, string> { ["7-Zip"] = "24.08", ["Old Tool"] = "1.0", ["Steam"] = "" });
    var seen = ChangeService.Load("apps-test.txt")!;
    var changes = ChangeService.Diff(seen, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["7-zip"] = "24.09", ["Steam"] = "", ["VLC"] = "3.0.21" });
    Check(changes.SequenceEqual(new[] { ("updated", "7-zip 24.08 → 24.09"), ("installed", "VLC 3.0.21"), ("removed", "Old Tool 1.0") }),
        "changes: installed, updated and removed apps, nothing for one that stayed the same: " + string.Join("; ", changes));

    // What a week says: memory short for two hours a day, a 5 GB app, an app busy all week, slower starts.
    {
        var nowUtc = DateTime.UtcNow;
        var week = new HistoryData(new(), new(), new());
        for (int mins = 0; mins < 7 * 24 * 60; mins += 5) // a reading every five minutes is plenty here
        {
            var t = nowUtc.AddDays(-7).AddMinutes(mins);
            bool full = mins % (24 * 60) < 600; // the first 10 hours of each day
            week.System.Add(new SysSample(t, 20, full ? 92 : 50, 5, 60, 40));
            week.Processes.Add(new ProcSample(t, "updater", 8, 100, 0));
            if (mins % 30 == 0) week.Processes.Add(new ProcSample(t, "editor", 3, 5200, 0));
            if (full) week.Processes.Add(new ProcSample(t, "browser", 4, 9000, 0));
        }
        var boots = new List<Boot>
        {
            new(nowUtc.AddDays(-1), 75, 30, 10, new() { new("NewSync", 20) }), new(nowUtc.AddDays(-3), 65, 30, 10, new()),
            new(nowUtc.AddDays(-9), 40, 25, 10, new() { new("OldThing", 5) }), new(nowUtc.AddDays(-11), 44, 25, 10, new()),
        };
        var said = InsightService.From(week, boots, nowUtc);
        Check(said.Any(x => x.StartsWith("Memory was over 85%") && x.Contains("browser")), "insights: memory running short, and who held it: " + said.FirstOrDefault());
        Check(said.Any(x => x.StartsWith("browser held 8.8 GB")) && said.Any(x => x.StartsWith("editor held 5.1 GB")), "insights: the apps that hold gigabytes");
        Check(said.Any(x => x.StartsWith("updater kept about 8% of the CPU busy")), "insights: an app busy all week");
        Check(said.Any(x => x.Contains("longer than the week before") && x.Contains("NewSync") && !x.Contains("OldThing")), "insights: Windows starting slower, and what's new holding it up");
        Check(InsightService.From(new HistoryData(new(), new(), new()), new(), nowUtc).Count == 0, "insights: nothing to say about nothing");
    }

    // 2. A finished day written by hand: an old 7-field line, a new 9-field one, a torn line, junk.
    var yesterday = DateTime.Today.AddDays(-1);
    long noon = Unix(yesterday.AddHours(12));
    var lines = new List<string>
    {
        $"S,{noon},5,30,1,60,40",                 // before 1.5: no disk/network
        $"S,{noon + 1},6,30,1,60,40,10,2",
        "garbage line",
        $"S,{noon + 2},100,31,2,70,41,250,90",    // the spike, inside the same minute
        $"S,{noon + 3},7,30,1,61,40,1,1,90,40,900,80",
        $"S,{noon + 5},7,30,1,61,40,1,1,150,60,1500,62",   // a laptop: power and fans peak, battery drains
        $"S,{noon + 6},7,30,1,61,40,1,1,90,40,900,80,144", // a game in front at 144 FPS…
        $"S,{noon + 7},7,30,1,61,40,1,1,90,40,900,80,38",  // …dropping to 38 for a second
        $"P,{noon + 6},game,20,4000,90,141",
        $"P,{noon},quiet,1,100,0",
        $"P,{noon + 2},burner,95,500,0",
        $"E,{noon + 2},sleep,",
        $"S,{noon + 4},8",                         // torn by a hard reset
    };
    File.WriteAllLines(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.csv"), lines);
    var raw = HistoryStore.ReadDay(yesterday, minutes: false);
    Check(raw.System.Count == 7, "old day: torn and junk lines skipped, seven samples read");
    Check(float.IsNaN(raw.System[0].Fps) && raw.System[5].Fps == 144, "fps: older lines read as unknown, new ones keep it");
    Check(raw.Processes.Single(p => p.Name == "game").Fps == 141 && float.IsNaN(raw.Processes.Single(p => p.Name == "quiet").Fps), "fps: an app's own rate round-trips, older app lines have none");
    Check(float.IsNaN(raw.System[0].DiskMBs) && float.IsNaN(raw.System[0].NetMbps), "old day: a 7-field line reads disk/network as unknown");
    Check(raw.System[1].DiskMBs == 10 && raw.System[1].NetMbps == 2, "old day: a 9-field line keeps disk/network");

    // 3. The minute summary keeps the peak of a 1-second spike, and the app behind it.
    var minutes = HistoryStore.ReadMinutes(yesterday.AddHours(11), yesterday.AddHours(13));
    Check(minutes.System.Count == 1, "minutes: one row for the minute");
    var m = minutes.System.SingleOrDefault();
    Check(m.Cpu == 100 && m.DiskMBs == 250 && m.NetMbps == 90 && m.CpuTemp == 70, "minutes: peaks survive, disk/network included");
    Check(m.CpuWatts == 150 && m.GpuWatts == 60 && m.FanRpm == 1500, "minutes: power and fan peaks survive");
    Check(m.BatteryPct == 62, "minutes: the battery keeps its lowest charge, not its highest");
    Check(m.Fps == 38, "minutes: FPS keeps the minute's lowest — the stutter, not the best");
    Check(minutes.Processes.Any(p => p.Name == "game" && p.Fps == 141), "minutes: an app's frame rate is kept");
    Check(minutes.Processes.Any(p => p.Name == "burner" && p.Cpu == 95), "minutes: the app behind the spike is kept at its peak");
    Check(minutes.Events.Count == 1 && minutes.Events[0].Kind == "sleep", "minutes: events copied through");
    Check(File.Exists(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.min.csv")), "minutes: summary file written for a finished day");

    // 4. Maintenance: finished days are packed (and still readable), gzipped ones from before 1.8 repacked,
    //    days past retention deleted.
    var old = DateTime.Today.AddDays(-40);
    File.WriteAllLines(Path.Combine(folder, $"{old:yyyy-MM-dd}.csv"), new[] { $"S,{Unix(old.AddHours(1))},1,1,1,1,1" });
    var gzDay = DateTime.Today.AddDays(-3);
    using (var gzOut = new StreamWriter(new GZipStream(File.Create(Path.Combine(folder, $"{gzDay:yyyy-MM-dd}.csv.gz")), CompressionLevel.Fastest)))
        gzOut.WriteLine($"S,{Unix(gzDay.AddHours(2))},33,1,1,1,1");
    HistoryStore.Maintain(DateTime.Today);
    string Day(DateTime d, string ext) => Path.Combine(folder, $"{d:yyyy-MM-dd}{ext}");
    Check(File.Exists(Day(yesterday, ".csv.br")) && !File.Exists(Day(yesterday, ".csv")), "maintain: yesterday packed");
    Check(File.Exists(Day(yesterday, ".min.csv.br")) && !File.Exists(Day(yesterday, ".min.csv")), "maintain: its minute summary packed too");
    Check(File.Exists(Day(gzDay, ".csv.br")) && !File.Exists(Day(gzDay, ".csv.gz")), "maintain: a gzipped day repacked");
    Check(HistoryStore.ReadDay(gzDay, minutes: false).System.SingleOrDefault().Cpu == 33, "maintain: the repacked day reads the same");
    Check(!File.Exists(Day(old, ".csv")), "maintain: a day past retention deleted");
    Check(HistoryStore.ReadDay(yesterday, minutes: false).System.Count == 7, "maintain: the packed day reads the same");
    Check(HistoryStore.ReadDay(yesterday, minutes: true).System.SingleOrDefault().Cpu == 100, "maintain: the packed summary reads the same");
    using (var br = new StreamReader(new BrotliStream(File.OpenRead(Day(yesterday, ".csv.br")), CompressionMode.Decompress)))
        Check(br.ReadToEnd().Contains("burner"), "maintain: the pack holds the original lines");

    // 5. Per-app totals: an app missing from a snapshot counts as 0 there.
    var t0 = DateTime.UtcNow;
    var totals = HistoryStore.TopProcesses(new[]
    {
        new ProcSample(t0, "a", 10, 100, 0), new ProcSample(t0, "b", 2, 50, 0),
        new ProcSample(t0.AddSeconds(10), "b", 4, 50, 0),
    });
    var a = totals.Single(t => t.Name == "a");
    Check(totals[0].Name == "a" && a.AvgCpu == 5 && a.PeakCpu == 10, "totals: average over every snapshot, peak kept");

    // 5b. The app index (1.17): a day recorded before it gets one at maintenance without its raw file being
    //     touched; an app missing from snapshots is "not recorded", not 0; sessions; the export's rows.
    {
        var d = DateTime.Today.AddDays(-2);
        long b = Unix(d.AddHours(10)); // on the 5-minute grid
        var dayLines = new List<string>();
        for (long t = b; t < b + 65 * 60; t += 10)
        {
            long min = (t - b) / 60;
            if (min >= 40 && min < 60) continue; // the PC was off
            dayLines.Add($"S,{t},20,50,2,60,40");
            dayLines.Add($"P,{t},other,1,200,0,");
            // editor: 0-15 min, a 5-minute slot away, 20-25, two slots away, 35-40, then again after the PC was off
            if (min < 15 || (min >= 20 && min < 25) || (min >= 35 && min < 40) || min >= 60)
                dayLines.Add($"P,{t},editor,{(min == 3 && t % 60 == b % 60 ? 40 : 4)},800,0,,3");
        }
        string rawPath = Path.Combine(folder, $"{d:yyyy-MM-dd}.csv.br");
        using (var w = new StreamWriter(new BrotliStream(File.Create(rawPath), CompressionLevel.Fastest))) foreach (var l in dayLines) w.WriteLine(l);
        byte[] before = File.ReadAllBytes(rawPath);
        HistoryStore.Maintain(DateTime.Today);
        Check(File.Exists(Path.Combine(folder, $"{d:yyyy-MM-dd}.apps.csv.br")) && File.ReadAllBytes(rawPath).AsSpan().SequenceEqual(before),
            "app index: built for a day packed before 1.17, its raw file byte for byte the same");

        var h = HistoryStore.ReadApps(d.AddHours(9), d.AddHours(13));
        var ed = HistoryStore.AppTotals(h).Single(t => t.Name == "editor");
        Check(h.SlotSeconds == 300 && h.Slots.Count == 9, $"app index: 5-minute slots, none where nothing was recorded ({h.Slots.Count})");
        Check(ed.Slots == 6 && ed.PeakCpu == 40 && Math.Abs(ed.Cpu - (40 + 4 * 179.0) / 180) < 0.01 && ed.Processes == 3 && ed.RamMB == 800,
            $"app index: averages over the snapshots it was in, not the range; peak and process count kept ({ed.Cpu:0.###})");
        var gap = h.Slots.Single(s => s.Time == DateTimeOffset.FromUnixTimeSeconds(b + 15 * 60).UtcDateTime);
        Check(gap.Snapshots == 30 && !h.Apps.Any(x => x.Name == "editor" && x.Time == gap.Time), "app index: a slot with snapshots but without the app has no row for it — not a 0");
        var appSessions = HistoryStore.AppSessions(h, "editor");
        Check(appSessions.Count == 3 && appSessions[0].Start == DateTimeOffset.FromUnixTimeSeconds(b).UtcDateTime && appSessions[0].End == DateTimeOffset.FromUnixTimeSeconds(b + 25 * 60).UtcDateTime
              && appSessions[1].End - appSessions[1].Start == TimeSpan.FromMinutes(5),
            "sessions: one missing slot doesn't split a session, two do, and so does the PC being off: " + string.Join("; ", appSessions.Select(x => $"{x.Start.ToLocalTime():HH:mm}-{x.End.ToLocalTime():HH:mm}")));
        var shortRange = HistoryStore.ReadApps(d.AddHours(10), d.AddHours(11));
        Check(shortRange.SlotSeconds == 30 && HistoryStore.AppTotals(shortRange).Single(t => t.Name == "editor").PeakCpu == 40, "app index: an hour comes from the snapshots, in 30-second slots");

        var csv = HistoryStore.AppCsv(h, "editor").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(csv.Length == 1 + 13 && csv[1].Contains(",recorded,") && csv[1].Contains(",3,20,50,2")
              && csv.Count(r => r.Contains(",not recorded,")) == 3 && csv.Count(r => r.Contains(",nothing recorded,")) == 4 && !csv.Any(r => r.Contains("other")),
            "export: a row per slot — recorded, not recorded, nothing recorded — and no other app: " + string.Join(" | ", csv.Select(r => r.Split(',')[1])));

        HistoryStore.RetentionDays = 1;
        HistoryStore.Maintain(DateTime.Today);
        Check(!File.Exists(Path.Combine(folder, $"{d:yyyy-MM-dd}.apps.csv.br")), "app index: removed with its day past retention");
        HistoryStore.RetentionDays = 30;
    }

    // 6. Remote monitoring: the protocol's guarantees, then a real server and client on localhost.
    var key = RemoteCrypto.ParseKey(RemoteCrypto.NewKey())!;
    var otherKey = RemoteCrypto.ParseKey(RemoteCrypto.NewKey())!;
    long nowT = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string path = "/sysoptimizer/v1/info";
    Check(key.Length == 32 && RemoteCrypto.ParseKey("not a key") == null, "remote: keys are 32 bytes, junk isn't one");
    Check(RemoteCrypto.Verify(key, path, nowT.ToString(), RemoteCrypto.Sign(key, path, nowT), DateTime.UtcNow), "remote: a fresh signed request passes");
    Check(!RemoteCrypto.Verify(otherKey, path, nowT.ToString(), RemoteCrypto.Sign(key, path, nowT), DateTime.UtcNow), "remote: the wrong key is refused");
    Check(!RemoteCrypto.Verify(key, path + "?x", nowT.ToString(), RemoteCrypto.Sign(key, path, nowT), DateTime.UtcNow), "remote: a signature can't be moved to another request");
    Check(!RemoteCrypto.Verify(key, path, (nowT - 300).ToString(), RemoteCrypto.Sign(key, path, nowT - 300), DateTime.UtcNow), "remote: a 5-minute-old request is refused (replay)");
    var sealedBytes = RemoteCrypto.Seal(key, "req", "secret history"u8.ToArray());
    Check(System.Text.Encoding.UTF8.GetString(RemoteCrypto.Open(key, "req", sealedBytes)) == "secret history", "remote: a sealed answer opens with the key");
    Check(!System.Text.Encoding.UTF8.GetString(sealedBytes).Contains("secret"), "remote: the answer isn't readable on the wire");
    bool Throws(Action act) { try { act(); return false; } catch (System.Security.Cryptography.CryptographicException) { return true; } }
    var tampered = (byte[])sealedBytes.Clone(); tampered[^1] ^= 1;
    Check(Throws(() => RemoteCrypto.Open(key, "req", tampered)), "remote: a tampered answer is rejected");
    Check(Throws(() => RemoteCrypto.Open(key, "other req", sealedBytes)), "remote: an answer to another request is rejected");
    Check(Throws(() => RemoteCrypto.Open(otherKey, "req", sealedBytes)), "remote: the wrong key can't open an answer");

    // Free for TCP (the server) and UDP (its discovery) alike: another app's UDP socket on the same number makes discovery miss.
    static int FreePort()
    {
        while (true)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int p = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try { using (new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, p))) return p; }
            catch (System.Net.Sockets.SocketException) { }
        }
    }
    int port = FreePort(), passwordPort = FreePort();
    var commands = new List<RemoteCommand>();
    string FakeRun(RemoteCommand c) { lock (commands) commands.Add(c); return "ok\ndone"; }
    var salt = RemoteCrypto.NewSalt();
    DateTime energyFrom = default, energyTo = default;
    var bigFile = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 << 20); // random: gzip can't shrink it, so the progress has megabytes to report
    using (new HistoryServer(port, key, salt, () => "{\"CpuPercent\":42}"u8.ToArray(), () => "[{\"Name\":\"app\"}]"u8.ToArray(),
        (from, to) => { (energyFrom, energyTo) = (from, to); return "chrome\t1.5"; }, FakeRun, name => name == "startup" ? "[\"startup list\"]"u8.ToArray() : null,
        (folder, path, name) => folder != "Downloads" ? null
            : name == null ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(path == "photos"
                ? new[] { new RemoteFiles.Entry("a b.txt", 3, DateTime.UtcNow) } : new[] { new RemoteFiles.Entry("photos", 0, DateTime.UtcNow, Folder: true) })
            : name == "photos/a b.txt" ? bigFile : null, host: "localhost"))
    {
        var client = new RemoteHistory("test", "localhost", port, key);
        Check(client.Hello() == Environment.MachineName, "remote: the server says who it is");
        Check(client.Live().Contains("\"CpuPercent\":42"), "remote: the live reading arrives");
        Check(client.Processes().Contains("\"app\""), "remote: the app list arrives");
        var at = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        Check(client.Energy(at, at.AddHours(1)) == "chrome\t1.5" && energyFrom == at && energyTo == at.AddHours(1), "remote: energy figures for the asked range");
        var first = client.ReadDay(DateTime.Today, minutes: false);
        var here = HistoryStore.ReadDay(DateTime.Today, minutes: false);
        Check(first.System.Count == here.System.Count && first.Events.Count == here.Events.Count, $"remote: today's lines arrive ({first.System.Count} S, {first.Events.Count} E)");
        // More of today, including a launch stamped earlier than the newest line (written ~10 s late).
        var later = DateTime.UtcNow.AddSeconds(2);
        HistoryStore.Write(new SysSample(later, 50, 41, 8, 66, 46, 1, 1));
        HistoryStore.WriteEvent("launch", "late-app", later.AddSeconds(-10));
        var second = client.ReadDay(DateTime.Today, minutes: false);
        Check(second.System.Count == first.System.Count + 1, "remote: only new lines added, none twice");
        Check(second.Events.Any(e => e.Detail == "late-app"), "remote: a launch written late isn't skipped");
        Check(client.ReadDay(DateTime.Today.AddDays(-1), minutes: true).System.Count == 1, "remote: a finished day's minute summary arrives");
        long heard = 0, of = 0;
        Check(client.Files("Downloads") is [{ Name: "photos", Folder: true }] && client.Files("Downloads", "photos") is [{ Name: "a b.txt", Bytes: 3 }]
              && client.CopyFile("Downloads", "photos/a b.txt", (done, total) => (heard, of) = (done, total)).SequenceEqual(bigFile),
            "files: a folder, a subfolder's list and a file's copy arrive");
        Check(of > 0 && heard == of, $"files: the download's progress is heard to the end ({heard} of {of})");
        long sent = 0, sentOf = 0;
        client.Run("receive-file", "big.bin", "", bigFile, (done, total) => (sent, sentOf) = (done, total));
        Check(sentOf > 1 << 20 && sent == sentOf && commands[^1].File!.SequenceEqual(bigFile), $"files: the upload's progress is heard to the end, the file intact ({sent} of {sentOf})");
        try { client.Files("Windows"); Check(false, "files: only the three folders"); }
        catch (RemoteHistoryException) { Check(true, "files: only the three folders"); }
        Check(RemoteFiles.FileOf("Downloads", "../secret.txt") == null && RemoteFiles.FileOf("Downloads", "a/./b.txt") == null && RemoteFiles.List("Downloads", "..") == null
              && RemoteFiles.List("Downloads", "no such folder " + Guid.NewGuid()) == null, "files: no way out of the three folders");
        Check(RemoteFiles.FileOf("Downloads", @"..\secret.txt") == null && RemoteFiles.FileOf("Downloads", "NUL") == null && RemoteFiles.FileOf("System32", "x.dll") == null
              && RemoteFiles.FileOf("Downloads", "no such file " + Guid.NewGuid() + ".txt") == null, "files: no paths, device names, other folders or missing files");

        var intruder = new RemoteHistory("x", "localhost", port, otherKey);
        try { intruder.Hello(); Check(false, "remote: the wrong key gets nothing"); }
        catch (RemoteHistoryException ex) { Check(ex.Message.Contains("refused"), "remote: the wrong key gets nothing"); }

        // A password: the salt is handed out, and the key it makes is the one the server holds.
        Check(RemoteHistory.SaltOf("localhost", port) is { } handed && handed.SequenceEqual(salt), "password: the salt is handed out without the key");
        var fromPassword = RemoteCrypto.FromPassword("correct horse battery", salt);
        Check(fromPassword.SequenceEqual(RemoteCrypto.FromPassword("correct horse battery", salt)) && !fromPassword.SequenceEqual(RemoteCrypto.FromPassword("correct horse battery", RemoteCrypto.NewSalt())),
            "password: same password + salt = same key; another PC's salt = another key");
        using (new HistoryServer(passwordPort, fromPassword, salt, host: "localhost"))
            Check(new RemoteHistory("p", "localhost", passwordPort, RemoteCrypto.FromPassword("correct horse battery", RemoteHistory.SaltOf("localhost", passwordPort)!)).Hello() == Environment.MachineName,
                "password: a watcher that knows only the password gets in");

        // Commands: each reaches the PC as asked; anything malformed or replayed is refused.
        Check(client.Run("install-winget", "Notepad++.Notepad++").StartsWith("ok") && commands[^1] is { Action: "install-winget", Target: "Notepad++.Notepad++" }, "command: a winget package reaches the installer");
        var setup = System.Security.Cryptography.RandomNumberGenerator.GetBytes(100_000);
        client.Run("install-file", "setup v2.exe", @"/S /D=C:\Program Files\X", setup);
        Check(commands[^1] is { Action: "install-file", Target: "setup v2.exe", Args: @"/S /D=C:\Program Files\X" } j && j.File!.SequenceEqual(setup), "command: an uploaded file arrives intact, with its options");
        client.Run("install-url", "https://example.com/setup.msi", "/quiet");
        Check(commands[^1] is { Action: "install-url", Target: "https://example.com/setup.msi", Args: "/quiet" }, "command: a link arrives with its options");
        foreach (var simple in new[] { "upgrade-all", "cleanup", "restart", "shutdown" }) client.Run(simple);
        client.Run("kill", "notepad");
        Check(commands.Skip(commands.Count - 5).Select(c => c.Action).SequenceEqual(new[] { "upgrade-all", "cleanup", "restart", "shutdown", "kill" }) && commands[^1].Target == "notepad",
            "command: update, clean up, restart, shut down, end an app");
        int before = commands.Count;
        foreach (var (what, target) in new[] { ("install-file", @"..\evil.exe"), ("install-url", "http://example.com/a.exe"), ("install-winget", "x;calc"), ("kill", @"..\x"), ("format-disk", null), ("restart", "now") })
            try { client.Run(what, target, "", what == "install-file" ? setup : null); } catch (RemoteHistoryException) { }
        Check(commands.Count == before, "command: paths, plain http, bad ids, unknown actions refused");
        Check(RemoteCommand.SafeFileName("a.bat") == null && RemoteCommand.SafeFileName("x.MSI") == "x.MSI", "command: only bare .exe/.msi names");
        client.Run("receive-file", "photos 2026.zip", "", setup);
        Check(commands[^1] is { Action: "receive-file", Target: "photos 2026.zip" } r && r.File!.SequenceEqual(setup), "command: a sent file arrives intact");
        before = commands.Count;
        foreach (var name in new[] { @"..\x.txt", "NUL", "con.txt", "COM1.log", "notes.", "a:b.txt" })
            try { client.Run("receive-file", name, "", setup); } catch (RemoteHistoryException) { }
        try { client.Run("receive-file", "ok.txt", "/S", setup); } catch (RemoteHistoryException) { }
        Check(commands.Count == before && RemoteCommand.PlainFileName("console.txt") == "console.txt", "command: sent files only under names Windows saves as typed, no options");
        // The same signed request sent twice runs once.
        string ip = HistoryServer.Prefix + "do?what=cleanup";
        long it = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var poster = new System.Net.Http.HttpClient();
        System.Net.HttpStatusCode Post()
        {
            using var m = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://localhost:{port}{ip}");
            m.Headers.Add("X-Time", it.ToString()); m.Headers.Add("X-Auth", RemoteCrypto.Sign(key, ip, it));
            return poster.Send(m).StatusCode;
        }
        var (once, again) = (Post(), Post());
        Check(once == System.Net.HttpStatusCode.OK && again == System.Net.HttpStatusCode.BadRequest && commands.Count == before + 1, $"command: a replayed request doesn't run again ({once}, {again}, {commands.Count - before})");

        // Lists and identity (1.9): the startup/services/boots lists, the version, and the MACs for waking it.
        Check(client.List("startup") == "[\"startup list\"]", "lists: an app list arrives by name");
        try { client.List("../secret"); Check(false, "lists: unknown names refused"); }
        catch (RemoteHistoryException) { Check(true, "lists: unknown names refused"); }
        var (version, macs) = client.Identity();
        Check(version.Length > 0 && macs.All(WakeOnLan.IsMac), "identity: version and wake-up addresses");
        foreach (var (what, target) in new[] { ("update-app", (string?)null), ("startup-disable", "Spotify"), ("service-enable", "SysMain") }) client.Run(what, target);
        Check(commands.Skip(commands.Count - 3).Select(c => (c.Action, c.Target)).SequenceEqual(new[] { ("update-app", (string?)null), ("startup-disable", "Spotify"), ("service-enable", "SysMain") }),
            "command: update the app, a startup entry, a service");
        int beforeBad = commands.Count;
        try { client.Run("service-disable", "bad\nname"); } catch (RemoteHistoryException) { }
        try { client.Run("update-app", "now"); } catch (RemoteHistoryException) { }
        Check(commands.Count == beforeBad, "command: control characters and stray targets refused");

        // Discovery: the probe on the sharing port is answered with the PC's name.
        var found = RemoteDiscovery.Find(port, TimeSpan.FromSeconds(1), System.Net.IPAddress.Loopback).GetAwaiter().GetResult();
        Check(found.Count == 1 && found[0].Name == Environment.MachineName && found[0].Address == "127.0.0.1", "discovery: a sharing PC answers with its name");
    }

    // 8. Wake-on-LAN's packet, and the phone page: its password, its lockout.
    var magic = WakeOnLan.MagicPacket("001122AABBCC");
    Check(magic.Length == 102 && magic.Take(6).All(b => b == 0xFF) && magic[6] == 0x00 && magic[11] == 0xCC && magic[^1] == 0xCC && magic[^6] == 0x00, "wake: 6 × FF, then the MAC 16 times");
    Check(!WakeOnLan.IsMac("00112233445") && !WakeOnLan.IsMac("00112233445G") && WakeOnLan.IsMac("00112233445f"), "wake: MACs are 12 hex digits");
    int phonePort = FreePort();
    var phoneSalt = RemoteCrypto.NewSalt();
    using (new PhoneServer(phonePort, RemoteCrypto.FromPassword("phone pass", phoneSalt), phoneSalt, () => "[{\"name\":\"pc\"}]"u8.ToArray(), "localhost"))
    {
        using var browser = new System.Net.Http.HttpClient();
        System.Net.Http.HttpResponseMessage Ask(string path, string? password)
        {
            var m = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://localhost:{phonePort}{path}");
            if (password != null) m.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("me:" + password)));
            return browser.Send(m);
        }
        var anonymous = Ask("/", null);
        Check(anonymous.StatusCode == System.Net.HttpStatusCode.Unauthorized && anonymous.Headers.WwwAuthenticate.ToString().Contains("Basic"), "phone: asks for the password");
        Check(Ask("/", "wrong").StatusCode == System.Net.HttpStatusCode.Unauthorized, "phone: a wrong password is refused");
        var page = Ask("/", "phone pass");
        Check(page.IsSuccessStatusCode && page.Content.ReadAsStringAsync().Result.Contains("Sysoptimizer"), "phone: the right password opens the page");
        Check(Ask("/api/pcs", "phone pass").Content.ReadAsStringAsync().Result == "[{\"name\":\"pc\"}]", "phone: the cards arrive");
        for (int i = 0; i < 9; i++) Ask("/", "guess " + i);
        Check(Ask("/", "another guess").StatusCode == (System.Net.HttpStatusCode)429, "phone: 10 wrong passwords lock it for a while");
        Check(Ask("/", "phone pass").StatusCode == System.Net.HttpStatusCode.OK, "phone: a browser already in stays in");
    }

    // 7. Windows' energy table and winget's search table, as the real tools print them.
    var energy = EnergyService.Parse(new StringReader(
        "AppId, UserId, TimeStamp, OnBattery, TotalEnergyConsumption\n" +
        @"\Device\HarddiskVolume3\Program Files\Google\Chrome\chrome.exe, S-1, 2026-10-12T08:00:00.0000, FALSE, 3600000" + "\n" +
        "Claude_2.19675.1.0_x64__pzs8sxrjxfjjc, S-1, 2026-10-12T08:01:00.0000, FALSE, 7200\n" +
        "EMI_RAPL_Package0_PKG, , 2026-10-12T08:00:00.0000, FALSE, 99999999\n" +
        @"\Device\HarddiskVolume3\a, b\x.exe, S-1, 2026-10-12T08:00:00.0000, FALSE, 5" + "\n"));
    Check(energy.Count == 2 && energy[0] is (var t1, "chrome", 3600000) && t1 == new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc) && t1.Kind == DateTimeKind.Utc
          && energy[1].Item2 == "Claude", "energy: apps named like the Processes tab, times in UTC, meters and broken rows skipped");
    var table = WingetService.ParseTable(" \r  - \rName                       Id                         Version    Match        Source\r\n" +
                                         "------------------------------------------------------------------------------\r\n" +
                                         "VLC media player           VideoLAN.VLC               3.0.24     Moniker: vlc winget\r\n" +
                                         "VLC media player (Nightly) VideoLAN.VLC.Nightly       4.0.0.0               winget\r\n");
    Check(table.Count == 2 && table[0] == ("VLC media player", "VideoLAN.VLC", "3.0.24") && table[1].Id == "VideoLAN.VLC.Nightly", "winget: the search table parsed by column position");

    // 8. The Protagonist Labs shared folder: the card, garbage tolerated, and Shelf's games' sessions from the history.
    string eco = Path.Combine(folder, "eco"), ecoHistory = Path.Combine(folder, "eco-history");
    Directory.CreateDirectory(ecoHistory);
    Ecosystem.Folder = eco;
    HistoryStore.Folder = ecoHistory;
    string self = Environment.ProcessPath!;
    Ecosystem.WriteCard(self, new Version(1, 14, 0));
    var card = Ecosystem.Card("sysoptimizer");
    Check(card?["version"]?.ToString() == "1.14.0" && card["edition"]?.ToString() == "free" && card["updated"] != null
          && !File.ReadAllBytes(Path.Combine(eco, "apps", "sysoptimizer.json")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "ecosystem: the card, schema 1, no BOM");
    File.WriteAllText(Path.Combine(eco, "apps", "shelf.json"), "{ not json");
    File.WriteAllText(Path.Combine(eco, "apps", "stash.json"), "{\"schema\":2,\"exe\":" + JsonSerializer.Serialize(self) + "}");
    File.WriteAllText(Path.Combine(eco, "apps", "reel.json"), "{\"schema\":1,\"paid\":true,\"edition\":\"full\",\"exe\":" + JsonSerializer.Serialize(@"C:\gone\Reel.exe") + "}");
    Check(Ecosystem.Card("shelf") == null && Ecosystem.Card("stash") == null && Ecosystem.Card("reel") == null && Ecosystem.Card("nobody") == null && !Ecosystem.BoughtSomething(),
          "ecosystem: broken, wrong-schema, uninstalled and missing cards read as absent");
    File.WriteAllText(Path.Combine(eco, "apps", "reel.json"), "{\"schema\":1,\"paid\":true,\"edition\":\"full\",\"exe\":" + JsonSerializer.Serialize(self) + "}");
    Check(Ecosystem.BoughtSomething(), "ecosystem: a bought app is seen");

    Directory.CreateDirectory(Path.Combine(eco, "data"));
    string shared = Path.Combine(eco, "data", "sysoptimizer.games.json");
    File.WriteAllText(Path.Combine(eco, "data", "shelf.games.json"), "garbage");
    Ecosystem.WriteGames();
    Check(!File.Exists(shared), "ecosystem: no games file without a readable shelf.games.json");

    var played = DateTime.Today.AddDays(-1);
    long y0 = Unix(played.AddHours(20));
    File.WriteAllText(Path.Combine(ecoHistory, $"{played:yyyy-MM-dd}.csv"),
        $"S,{y0},50,40,90,70,60\nP,{y0},Game,30,4000,90,60\nP,{y0},chrome,5,900,1,\nP,{y0 + 10},game,30,4000,90,120\nP,{y0 + 70},Game,30,4000,90,\nP,{y0 + 80},Game,30,4000,90,0.4\n");
    long t1h = Unix(DateTime.Today.AddHours(1));
    File.WriteAllText(Path.Combine(ecoHistory, $"{DateTime.Today:yyyy-MM-dd}.csv"), $"P,{t1h},Game,30,4000,90,30\nP,{t1h + 600},Game,3"); // a torn last line
    File.WriteAllText(Path.Combine(ecoHistory, $"{DateTime.Today.AddDays(-30):yyyy-MM-dd}.csv"), $"P,{Unix(DateTime.Today.AddDays(-30).AddHours(1))},Game,30,4000,90,30\n");
    File.WriteAllText(Path.Combine(eco, "data", "shelf.games.json"), JsonSerializer.Serialize(new
    {
        schema = 1,
        games = new object[] { new { id = "g1", name = "My Game", exe = @"C:\Games\Game.exe" }, new { id = "g2", name = "Never run", exe = @"C:\x\Other.exe" }, new { id = "g3", name = "No exe" } },
    }));
    Ecosystem.WriteGames();
    var sessions = (Ecosystem.Read(shared)?["sessions"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n!.AsObject()).ToList() ?? new();
    Check(sessions.Count == 2 && sessions.All(x => x["name"]!.ToString() == "My Game" && x["exe"]!.ToString() == @"C:\Games\Game.exe"), "ecosystem: one session per day played, for Shelf's game only, 30 days back");
    Check(sessions.Count == 2 && sessions[0]["date"]!.ToString() == $"{played:yyyy-MM-dd}" && (int)sessions[0]["minutes"]! == 2
          && (double)sessions[0]["avgFps"]! == 90 && (double)sessions[0]["maxFps"]! == 120, "ecosystem: minutes seen, average and best FPS (blank and under 1 ignored, name case ignored)");
    Check(sessions.Count == 2 && (int)sessions[1]["minutes"]! == 1 && (double)sessions[1]["avgFps"]! == 30, "ecosystem: today counts, its unfinished last line doesn't");

    // Controllers' batteries for Shelf (1.18): only with Shelf's card, only the controllers, percent or level as read.
    string pads = Path.Combine(eco, "data", "sysoptimizer.controllers.json");
    var readAt = new DateTime(2026, 10, 20, 12, 0, 5, DateTimeKind.Utc).ToLocalTime();
    var devices = new List<Sysoptimizer.Models.PeripheralBattery>
    {
        new() { Name = "DualSense Wireless Controller", Via = "Bluetooth", Percent = 64, ReadAt = readAt },
        new() { Name = "8BitDo Pro 2", Via = "Bluetooth", Percent = 30, ReadAt = readAt },
        new() { Name = "WH-1000XM4", Via = "Bluetooth", Percent = 80, ReadAt = readAt },
        new() { Name = "Xbox controller", Via = "XInput", Level = "Low", ReadAt = readAt },
        new() { Name = "DualShock 4", Via = "PlayStation, USB", Percent = 70, Charging = true, ReadAt = readAt },
        new() { Name = "DualSense", Via = "PlayStation, Bluetooth", ReadAt = readAt }, // asleep: unknown
        new() { Name = "G502 X", Via = "Logitech HID++ (receiver)", Percent = 50, ReadAt = readAt },
    };
    File.WriteAllText(Path.Combine(eco, "apps", "shelf.json"), "{\"schema\":1,\"exe\":" + JsonSerializer.Serialize(@"C:\gone\Shelf.exe") + "}");
    Ecosystem.WriteControllers(devices);
    // A card pointing at a missing exe falls back to the Uninstall entries, which on a PC with Shelf installed find it.
    Check(File.Exists(pads) == (Ecosystem.SisterExe("shelf", "Shelf") != null), "controllers: nothing written while Shelf isn't installed (card gone, no Uninstall entry)");
    if (File.Exists(pads)) File.Delete(pads);
    Check(Ecosystem.SisterExe("nobody", "No Such App 9") == null, "sisters: neither a card nor an Uninstall entry is not installed");
    File.WriteAllText(Path.Combine(eco, "apps", "shelf.json"), "{\"schema\":1,\"exe\":" + JsonSerializer.Serialize(self) + "}");
    Check(Ecosystem.SisterExe("shelf", "Shelf") == self, "sisters: the card comes first");
    Ecosystem.WriteControllers(devices);
    var padFile = Ecosystem.Read(pads);
    var padList = (padFile?["controllers"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n!.AsObject()).ToList() ?? new();
    Check(padFile?["updated"] != null && padList.Select(c => c["name"]!.ToString()).SequenceEqual(new[] { "DualSense Wireless Controller", "8BitDo Pro 2", "Xbox controller", "DualShock 4", "DualSense" }),
          "controllers: XInput, PlayStation and Bluetooth pads by name; a headset and a Logitech mouse left out");
    Check(padList.Count == 5 && (int)padList[0]["percent"]! == 64 && padList[0]["level"] == null && padList[2]["percent"] == null && padList[2]["level"]!.ToString() == "Low"
          && (bool)padList[3]["charging"]! && padList[4]["percent"] == null && padList[4]["level"] == null && padList[0]["readAt"]!.ToString() == "2026-10-20T12:00:05Z"
          && padList[0]["via"]!.ToString() == "Bluetooth", "controllers: percent or level (null when not said), charging, readAt in UTC");
    Ecosystem.WriteControllers(new List<Sysoptimizer.Models.PeripheralBattery>());
    Check(Ecosystem.Read(pads)?["controllers"] is System.Text.Json.Nodes.JsonArray { Count: 0 }, "controllers: none connected is an empty list, not a stale one");

    // A failed update check is a failure, never "nothing newer": offline, 403 (GitHub's rate limit), 5xx and an
    // unreadable answer all throw; a readable answer with nothing newer returns null.
    bool CheckFails(string feed)
    {
        Updater.Feed = feed;
        try { Updater.Check(manual: true).GetAwaiter().GetResult(); return false; }
        catch (Exception e) when (Updater.Failed(e)) { return true; }
    }
    static string Serve(int status, string body)
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() =>
        {
            var c = listener.GetContext();
            c.Response.StatusCode = status;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes);
            c.Response.Close(); // the listener is left to the process: stopping it here can cut the answer off
        });
        return $"http://localhost:{port}/";
    }
    // Wireless batteries: the byte layouts, and the HID++ allow-list that keeps every request a read.
    Check(PeripheralReports.HidppAllowed(PeripheralReports.Root, 0) && PeripheralReports.HidppAllowed(PeripheralReports.UnifiedBattery, 1)
          && !PeripheralReports.HidppAllowed(PeripheralReports.UnifiedBattery, 2) && !PeripheralReports.HidppAllowed(0x8070, 1) // a setter, lighting
          && !PeripheralReports.HidppAllowed(PeripheralReports.Root, 1) && !PeripheralReports.HidppAllowed(0x00D0, 0), "HID++: only the listed reads are allowed (no setters, lighting or firmware)");
    var unified = PeripheralReports.Unified(new byte[] { 72, 4, 0, 0 });
    Check(unified.Percent == 72 && unified.Level == "Good" && !unified.Charging, "HID++ 0x1004: 72%, good, discharging");
    var levelsOnly = PeripheralReports.Unified(new byte[] { 0, 2, 1, 1 });
    Check(double.IsNaN(levelsOnly.Percent) && levelsOnly.Level == "Low" && levelsOnly.Charging, "HID++ 0x1004: a device without a percentage gives its level; charging");
    Check(PeripheralReports.Status(new byte[] { 55, 50, 0 }) == (55, false) && PeripheralReports.Status(new byte[] { 90, 0, 1 }).Charging, "HID++ 0x1000: level and recharging");
    var volts = PeripheralReports.Voltage(new byte[] { 0x0F, 0x50, 0x00 }); // 3920 mV
    Check(volts.Millivolts == 3920 && volts.Percent == 70 && !volts.Charging && PeripheralReports.Voltage(new byte[] { 0x10, 0x04, 0x80 }).Charging, "HID++ 0x1001: millivolts to percent, external power bit");
    Check(PeripheralReports.VoltagePercent(3000) == 0 && PeripheralReports.VoltagePercent(4300) == 100 && PeripheralReports.VoltagePercent(3850) == 55, "Li-ion curve: clamps and interpolates");
    var ds4 = new byte[64]; ds4[0] = 0x01; ds4[30] = 0x07;
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualShock4, ds4) == (true, 70, false, false), "DS4 USB: level 7 = 70%, no cable");
    ds4[30] = 0x1B;
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualShock4, ds4) == (true, 100, false, false), "DS4 USB: cable and level 11 = full, not charging");
    ds4[30] = 0x00;
    Check(!PeripheralReports.SonyBattery(PeripheralReports.Sony.DualShock4, ds4).Known, "DS4 clone: a zero battery byte reads as unknown");
    var ds4Bt = new byte[78]; ds4Bt[0] = 0x11; ds4Bt[32] = 0x05;
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualShock4, ds4Bt) == (true, 50, false, true), "DS4 Bluetooth full report: byte 32");
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualShock4, new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x08, 0, 0, 0, 0 }) is { Known: false, Bluetooth: true }, "DS4 Bluetooth short report: no battery in it");
    var dualSense = new byte[64]; dualSense[0] = 0x01; dualSense[53] = 0x14;
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualSenseEdge, dualSense) == (true, 40, true, false), "DualSense USB: level 4, charging");
    var dualSenseBt = new byte[78]; dualSenseBt[0] = 0x31; dualSenseBt[54] = 0x2A;
    Check(PeripheralReports.SonyBattery(PeripheralReports.Sony.DualSense, dualSenseBt) == (true, 100, true, true), "DualSense Bluetooth: byte 54, full");
    Check(PeripheralReports.SonyModel(0x0DF2) == PeripheralReports.Sony.DualSenseEdge && PeripheralReports.SonyModel(0x0268) == PeripheralReports.Sony.None, "Sony: Edge known, a DualShock 3 ignored");
    Check(PeripheralReports.XInputLevel(0, 3) == null && PeripheralReports.XInputLevel(1, 0) == "Wired" && PeripheralReports.XInputLevel(3, 1) == "Low", "XInput: disconnected, wired, NiMH low");

    Check(CheckFails("http://127.0.0.1:9/"), "update check: offline is a failure");
    Check(CheckFails(Serve(403, "{\"message\":\"API rate limit exceeded\"}")), "update check: 403 (rate limit) is a failure");
    Check(CheckFails(Serve(502, "")), "update check: 5xx is a failure");
    Check(CheckFails(Serve(200, "<html>")), "update check: an unreadable answer is a failure");
    Updater.Feed = Serve(200, "{\"tag_name\":\"v0.0.1\",\"body\":\"\",\"assets\":[{\"name\":\"Sysoptimizer-0.0.1-setup.exe\",\"browser_download_url\":\"x\",\"size\":1},{\"name\":\"Sysoptimizer.exe\",\"browser_download_url\":\"x\",\"size\":1}]}");
    Check(Updater.Check(manual: true).GetAwaiter().GetResult() == null, "update check: nothing newer is not a failure");
}
catch (Exception ex)
{
    Check(false, $"threw {ex.GetType().Name}: {ex.Message}");
}

// What's new reads whatsnew.txt from the exe: every release header must parse, newest first.
using (var notes = new StreamReader(typeof(Updater).Assembly.GetManifestResourceStream("Sysoptimizer.whatsnew.txt")!))
{
    var heads = notes.ReadToEnd().Split('\n').Where(l => l.StartsWith("## ")).Select(l => l[3..].Split(" | ", 3)).ToList();
    bool parsed = heads.Count > 0 && heads.All(h => h.Length == 3 && Version.TryParse(h[0].Trim(), out _)
        && DateTime.TryParseExact(h[1].Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _));
    Check(parsed, "what's new: every release header parses");
    Check(parsed && heads.Zip(heads.Skip(1)).All(p => Version.Parse(p.First[0].Trim()) > Version.Parse(p.Second[0].Trim())), "what's new: newest first");
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } // today's file is still held open by the writer
return failures == 0 ? 0 : 1;
