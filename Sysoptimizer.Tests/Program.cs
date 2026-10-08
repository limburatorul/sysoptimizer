using System.IO.Compression;
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

string folder = Path.Combine(Path.GetTempPath(), "sysoptimizer-selftest-" + Environment.ProcessId);
Directory.CreateDirectory(folder);
HistoryStore.Folder = folder;
HistoryStore.RetentionDays = 30;

try
{
    // 1. Today: write a sample, its apps and an event, read them back at full resolution.
    var now = DateTime.UtcNow;
    HistoryStore.Write(new SysSample(now, 12.5f, 40, 7, 65, 45, 33.3f, 8.5f),
        new[] { new ProcSample(now, "chrome", 5.5f, 1200, 2), new ProcSample(now, "game,with comma", 1, 300, 50) });
    HistoryStore.WriteEvent("launch", "notepad", now);
    var today = HistoryStore.Read(now.AddMinutes(-1), now.AddMinutes(1));
    var s = today.System.SingleOrDefault();
    Check(today.System.Count == 1, "today: one sample read back");
    Check(s.Cpu == 12.5f && s.Mem == 40 && s.Gpu == 7 && s.CpuTemp == 65 && s.GpuTemp == 45, "today: cpu/mem/gpu/temps round-trip");
    Check(Math.Abs(s.DiskMBs - 33.3f) < 0.01 && s.NetMbps == 8.5f, "today: disk and network round-trip");
    Check(today.Processes.Count == 2 && today.Processes.Any(p => p.Name == "game_with comma" && p.Gpu == 50), "today: apps round-trip, a comma in a name can't split the line");
    Check(today.Events.Count == 1 && today.Events[0].Kind == "launch" && today.Events[0].Detail == "notepad", "today: event round-trip");

    // 2. A finished day written by hand: an old 7-field line, a new 9-field one, a torn line, junk.
    var yesterday = DateTime.Today.AddDays(-1);
    long noon = Unix(yesterday.AddHours(12));
    var lines = new List<string>
    {
        $"S,{noon},5,30,1,60,40",                 // before 1.5: no disk/network
        $"S,{noon + 1},6,30,1,60,40,10,2",
        "garbage line",
        $"S,{noon + 2},100,31,2,70,41,250,90",    // the spike, inside the same minute
        $"S,{noon + 3},7,30,1,61,40,1,1",
        $"P,{noon},quiet,1,100,0",
        $"P,{noon + 2},burner,95,500,0",
        $"E,{noon + 2},sleep,",
        $"S,{noon + 4},8",                         // torn by a hard reset
    };
    File.WriteAllLines(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.csv"), lines);
    var raw = HistoryStore.ReadDay(yesterday, minutes: false);
    Check(raw.System.Count == 4, "old day: torn and junk lines skipped, four samples read");
    Check(float.IsNaN(raw.System[0].DiskMBs) && float.IsNaN(raw.System[0].NetMbps), "old day: a 7-field line reads disk/network as unknown");
    Check(raw.System[1].DiskMBs == 10 && raw.System[1].NetMbps == 2, "old day: a 9-field line keeps disk/network");

    // 3. The minute summary keeps the peak of a 1-second spike, and the app behind it.
    var minutes = HistoryStore.ReadMinutes(yesterday.AddHours(11), yesterday.AddHours(13));
    Check(minutes.System.Count == 1, "minutes: one row for the minute");
    var m = minutes.System.SingleOrDefault();
    Check(m.Cpu == 100 && m.DiskMBs == 250 && m.NetMbps == 90 && m.CpuTemp == 70, "minutes: peaks survive, disk/network included");
    Check(minutes.Processes.Any(p => p.Name == "burner" && p.Cpu == 95), "minutes: the app behind the spike is kept at its peak");
    Check(minutes.Events.Count == 1 && minutes.Events[0].Kind == "sleep", "minutes: events copied through");
    Check(File.Exists(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.min.csv")), "minutes: summary file written for a finished day");

    // 4. Maintenance: finished days are gzipped (and still readable), days past retention deleted.
    var old = DateTime.Today.AddDays(-40);
    File.WriteAllLines(Path.Combine(folder, $"{old:yyyy-MM-dd}.csv"), new[] { $"S,{Unix(old.AddHours(1))},1,1,1,1,1" });
    HistoryStore.Maintain(DateTime.Today);
    Check(File.Exists(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.csv.gz")) && !File.Exists(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.csv")), "maintain: yesterday gzipped");
    Check(!File.Exists(Path.Combine(folder, $"{old:yyyy-MM-dd}.csv")), "maintain: a day past retention deleted");
    Check(HistoryStore.ReadDay(yesterday, minutes: false).System.Count == 4, "maintain: the gzipped day reads the same");
    using (var gz = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(folder, $"{yesterday:yyyy-MM-dd}.csv.gz")), CompressionMode.Decompress)))
        Check(gz.ReadToEnd().Contains("burner"), "maintain: gzip holds the original lines");

    // 5. Per-app totals: an app missing from a snapshot counts as 0 there.
    var t0 = DateTime.UtcNow;
    var totals = HistoryStore.TopProcesses(new[]
    {
        new ProcSample(t0, "a", 10, 100, 0), new ProcSample(t0, "b", 2, 50, 0),
        new ProcSample(t0.AddSeconds(10), "b", 4, 50, 0),
    });
    var a = totals.Single(t => t.Name == "a");
    Check(totals[0].Name == "a" && a.AvgCpu == 5 && a.PeakCpu == 10, "totals: average over every snapshot, peak kept");

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

    int port;
    { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
    using (new HistoryServer(port, key, () => "{\"CpuPercent\":42}"u8.ToArray(), "localhost"))
    {
        var client = new RemoteHistory("test", "localhost", port, key);
        Check(client.Hello() == Environment.MachineName, "remote: the server says who it is");
        Check(client.Live().Contains("\"CpuPercent\":42"), "remote: the live reading arrives");
        var first = client.ReadDay(DateTime.Today, minutes: false);
        Check(first.System.Count == 1 && first.Events.Count == 1, "remote: today's lines arrive");
        // More of today, including a launch stamped earlier than the newest line (written ~10 s late).
        var later = DateTime.UtcNow.AddSeconds(2);
        HistoryStore.Write(new SysSample(later, 50, 41, 8, 66, 46, 1, 1));
        HistoryStore.WriteEvent("launch", "late-app", later.AddSeconds(-10));
        var second = client.ReadDay(DateTime.Today, minutes: false);
        Check(second.System.Count == 2, "remote: only new lines added, none twice");
        Check(second.Events.Any(e => e.Detail == "late-app"), "remote: a launch written late isn't skipped");
        Check(client.ReadDay(DateTime.Today.AddDays(-1), minutes: true).System.Count == 1, "remote: a finished day's minute summary arrives");

        var intruder = new RemoteHistory("x", "localhost", port, otherKey);
        try { intruder.Hello(); Check(false, "remote: the wrong key gets nothing"); }
        catch (RemoteHistoryException ex) { Check(ex.Message.Contains("refused"), "remote: the wrong key gets nothing"); }
    }
}
catch (Exception ex)
{
    Check(false, $"threw {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } // today's file is still held open by the writer
return failures == 0 ? 0 : 1;
