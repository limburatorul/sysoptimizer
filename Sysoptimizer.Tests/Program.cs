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

foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "sysoptimizer-selftest-*")) // earlier runs can't delete their own (today's file is open)
    try { Directory.Delete(old, recursive: true); } catch (IOException) { } // another run still has it
string folder = Path.Combine(Path.GetTempPath(), "sysoptimizer-selftest-" + Guid.NewGuid().ToString("N")); // never a leftover from an earlier run
Directory.CreateDirectory(folder);
HistoryStore.Folder = folder;
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
    using (new HistoryServer(port, key, salt, () => "{\"CpuPercent\":42}"u8.ToArray(), () => "[{\"Name\":\"app\"}]"u8.ToArray(),
        (from, to) => { (energyFrom, energyTo) = (from, to); return "chrome\t1.5"; }, FakeRun, name => name == "startup" ? "[\"startup list\"]"u8.ToArray() : null,
        (folder, name) => folder != "Downloads" ? null
            : name == null ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { new RemoteFiles.Entry("a b.txt", 3, DateTime.UtcNow) })
            : name == "a b.txt" ? "abc"u8.ToArray() : null, host: "localhost"))
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
        Check(client.Files("Downloads") is [{ Name: "a b.txt", Bytes: 3 }] && client.CopyFile("Downloads", "a b.txt").SequenceEqual("abc"u8.ToArray()),
            "files: a folder's list and a file's copy arrive");
        try { client.Files("Windows"); Check(false, "files: only the three folders"); }
        catch (RemoteHistoryException) { Check(true, "files: only the three folders"); }
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
}
catch (Exception ex)
{
    Check(false, $"threw {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
try { Directory.Delete(folder, recursive: true); } catch (IOException) { } // today's file is still held open by the writer
return failures == 0 ? 0 : 1;
