using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>What runs on its own: scheduled actions, the weekly report, the cooling check, and the phone page.</summary>
public partial class MainWindow
{
    private const string ScheduleKey = @"Software\Sysoptimizer\Schedule";
    private const string PhoneKey = @"Software\Sysoptimizer\Phone";
    private const string PhoneFirewallRule = "Sysoptimizer phone page";
    private const int DefaultPhonePort = 7780;

    private static readonly (string Action, string Label)[] ScheduleActions =
    {
        ("upgrade-all", "Update all apps"), ("cleanup", "Clean up temporary files"), ("update-app", "Update Sysoptimizer"),
        ("restart", "Restart"), ("shutdown", "Shut down"), ("wake", "Wake up (watched PCs)"),
    };
    private static readonly (string Days, string Label)[] ScheduleDays =
    {
        ("daily", "Every day"), ("weekdays", "Monday to Friday"), ("weekends", "Saturday and Sunday"),
        ("Monday", "Mondays"), ("Tuesday", "Tuesdays"), ("Wednesday", "Wednesdays"), ("Thursday", "Thursdays"),
        ("Friday", "Fridays"), ("Saturday", "Saturdays"), ("Sunday", "Sundays"),
    };

    /// <param name="Pc">"" = this PC, "*" = every watched PC at the time it runs.</param>
    private sealed record ScheduleEntry(string Id, string Pc, string Action, string Days, TimeSpan At, string LastRun);

    private readonly List<ScheduleEntry> _schedule = new();
    private const string AllWatchedPcs = "Every watched PC";
    private PhoneServer? _phone;
    private ResourceSnapshot? _localSnap; // this PC's latest reading, for the phone page

    private void InitAutomation()
    {
        ScheduleActionCombo.ItemsSource = ScheduleActions.Select(a => a.Label).ToList();
        ScheduleActionCombo.SelectedIndex = 0;
        ScheduleDaysCombo.ItemsSource = ScheduleDays.Select(d => d.Label).ToList();
        ScheduleDaysCombo.SelectedIndex = 0;
        using (var key = Registry.CurrentUser.OpenSubKey(ScheduleKey))
            foreach (var id in key?.GetValueNames() ?? Array.Empty<string>())
                if (key!.GetValue(id) is string data && data.Split('|') is [var pc, var action, var days, var at, var last]
                    && TimeSpan.TryParseExact(at, @"hh\:mm", null, out var time))
                    _schedule.Add(new ScheduleEntry(id, pc, action, days, time, last));
        RefreshSchedule();

        // Every 30 s: what's due runs; once an hour: the weekly report; once a day: the cooling check.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        DateTime nextReport = DateTime.Now.AddMinutes(2), nextCooling = DateTime.Now.AddMinutes(5);
        timer.Tick += (_, _) =>
        {
            RunDueSchedule();
            if (DateTime.Now >= nextReport) { nextReport = DateTime.Now.AddHours(1); _ = WeeklyReportIfDue(); }
            if (DateTime.Now >= nextCooling) { nextCooling = DateTime.Now.AddDays(1); _ = CheckCooling(); }
        };
        timer.Start();

        using (var key = Registry.CurrentUser.OpenSubKey(PhoneKey))
        {
            PhonePortBox.Text = (key?.GetValue("Port") is int port ? port : DefaultPhonePort).ToString();
            PhoneCheck.IsChecked = key?.GetValue("On") is 1;
        }
        if (PhoneCheck.IsChecked == true) StartPhone();
        else PhoneStatus.Text = "Off.";
        Closed += (_, _) => _phone?.Dispose();
    }

    // --- schedule ---

    private void RefreshSchedule()
    {
        SchedulePcCombo.ItemsSource = new[] { "This PC" }.Concat(_remotes.Count > 0 ? new[] { AllWatchedPcs } : Array.Empty<string>()).Concat(_remotes.Select(r => r.Name)).ToList();
        if (SchedulePcCombo.SelectedIndex < 0) SchedulePcCombo.SelectedIndex = 0;
        ScheduleList.ItemsSource = _schedule.OrderBy(e => e.At).Select(e => new OptionItem
        {
            Name = $"{ScheduleActions.First(a => a.Action == e.Action).Label} — {e.Pc switch { "" => "this PC", "*" => "every watched PC", var pc => pc }}",
            Description = $"{ScheduleDays.FirstOrDefault(d => d.Days == e.Days).Label ?? e.Days} at {e.At:hh\\:mm}",
            Note = e.LastRun.Length > 0 ? $"last ran {e.LastRun}" : "",
            Tag = e.Id,
        }).ToList();
    }

    private void ScheduleAdd_Click(object sender, RoutedEventArgs e)
    {
        string pc = SchedulePcCombo.SelectedIndex <= 0 ? "" : SchedulePcCombo.SelectedItem as string == AllWatchedPcs ? "*" : (string)SchedulePcCombo.SelectedItem;
        var action = ScheduleActions[Math.Max(0, ScheduleActionCombo.SelectedIndex)].Action;
        var days = ScheduleDays[Math.Max(0, ScheduleDaysCombo.SelectedIndex)].Days;
        if (!TimeSpan.TryParseExact(ScheduleTimeBox.Text.Trim(), @"h\:mm", null, out var at) || at >= TimeSpan.FromDays(1))
        {
            ScheduleStatus.Text = "Type the time as hours:minutes, like 3:00 or 22:30.";
            return;
        }
        if (action == "wake" && pc.Length == 0) { ScheduleStatus.Text = "Waking up is for a watched PC — this one is already on when the schedule runs."; return; }
        var entry = new ScheduleEntry(Guid.NewGuid().ToString("N")[..8], pc, action, days, at, "");
        _schedule.Add(entry);
        SaveScheduleEntry(entry);
        ScheduleStatus.Text = action is "restart" or "shutdown"
            ? "Added. Whoever is using that PC then gets a minute's notice." : "Added.";
        RefreshSchedule();
    }

    private void ScheduleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _schedule.RemoveAll(s => s.Id == id);
        using (var key = Registry.CurrentUser.CreateSubKey(ScheduleKey)) key.DeleteValue(id, throwOnMissingValue: false);
        RefreshSchedule();
    }

    private static void SaveScheduleEntry(ScheduleEntry e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ScheduleKey);
        key.SetValue(e.Id, $"{e.Pc}|{e.Action}|{e.Days}|{e.At:hh\\:mm}|{e.LastRun}");
    }

    private static bool OnDay(string days, DayOfWeek day) => days switch
    {
        "daily" => true,
        "weekdays" => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        "weekends" => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
        _ => days == day.ToString(),
    };

    /// <summary>Runs each entry once on its day, within 10 minutes of its time (a PC that was off then skips it).</summary>
    private void RunDueSchedule()
    {
        var now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd");
        for (int i = 0; i < _schedule.Count; i++)
        {
            var e = _schedule[i];
            if (e.LastRun == today || !OnDay(e.Days, now.DayOfWeek) || now.TimeOfDay < e.At || now.TimeOfDay > e.At + TimeSpan.FromMinutes(10)) continue;
            _schedule[i] = e = e with { LastRun = today };
            SaveScheduleEntry(e); // before running: a restart must not run again when the PC comes back
            string label = ScheduleActions.First(a => a.Action == e.Action).Label;
            if (e.Pc.Length == 0)
            {
                Log($"Schedule: {label} on this PC.");
                var command = new RemoteCommand(e.Action, null, null, "");
                _ = Task.Run(() => RunCommand(command, scheduled: true));
            }
            else
            {
                var targets = e.Pc == "*" ? _remotes.ToList() : _remotes.Where(r => r.Name == e.Pc).ToList();
                if (targets.Count == 0) Log($"Schedule: {(e.Pc == "*" ? "no PC is watched" : $"{e.Pc} isn't watched any more")} — {label} skipped.");
                foreach (var target in targets)
                    _ = e.Action == "wake" ? Wake(target) : RunOn(target, $"Schedule: {label}", t => t.Run(e.Action));
            }
        }
        if (ScheduleList.IsVisible) RefreshSchedule();
    }

    private async Task Wake(RemoteHistory target)
    {
        if (target.Macs.Length == 0) { Log($"Can't wake {target.Name}: its network address isn't known yet — it's learned once the PC answers with Sysoptimizer 1.9 or newer."); return; }
        await WakeOnLan.Send(target.Macs);
        Log($"Wake-up sent to {target.Name}. If it stays off, Wake-on-LAN must be allowed in its BIOS and network adapter settings.");
    }

    // --- weekly report ---

    /// <summary>Mondays from 9:00, once: last week's report, announced in the tray.</summary>
    private async Task WeeklyReportIfDue()
    {
        var now = DateTime.Now;
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        if (now < monday.AddHours(9)) return;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer"))
        {
            if (key.GetValue("LastReport") as string == monday.ToString("yyyy-MM-dd")) return;
            key.SetValue("LastReport", monday.ToString("yyyy-MM-dd"));
        }
        string? path = await MakeReport(monday.AddDays(-7), monday);
        if (path == null) return;
        _balloonOpens = path;
        _tray?.ShowBalloonTip(10000, "Sysoptimizer", "Your PCs' weekly report is ready — click to open it.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (await MakeReport(DateTime.Now.Date.AddDays(-6), DateTime.Now) is { } path)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>This PC and every watched one, over [from, to): saved under Documents\Sysoptimizer; null if it couldn't be.</summary>
    private async Task<string?> MakeReport(DateTime from, DateTime to)
    {
        Log("Making the report…");
        var remotes = _remotes.ToList();
        var parts = await Task.Run(() =>
        {
            DateTime fromUtc = from.ToUniversalTime(), toUtc = to.ToUniversalTime();
            List<Boot>? Week(List<Boot>? boots) => boots?.Where(b => b.StartUtc >= fromUtc && b.StartUtc < toUtc).ToList();
            var list = new List<ReportPart>();
            List<Boot>? localBoots;
            try { localBoots = BootService.Read(); }
            catch (UnauthorizedAccessException) { localBoots = null; }
            list.Add(new ReportPart($"{Environment.MachineName} (this PC)", HistoryStore.ReadMinutes(from, to), Week(localBoots), null));
            foreach (var r in remotes)
                try
                {
                    List<Boot>? boots = null;
                    try { boots = JsonSerializer.Deserialize<List<Boot>>(r.List("boots"), LiveJson); }
                    catch (Exception ex) when (ex is RemoteHistoryException or JsonException) { } // older Sysoptimizer there: no boots
                    list.Add(new ReportPart(r.Name, r.ReadMinutes(from, to), Week(boots), null));
                }
                catch (RemoteHistoryException ex) { list.Add(new ReportPart(r.Name, null, null, ex.Message)); }
            return list;
        });
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sysoptimizer");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"Report {from:yyyy-MM-dd} to {to.AddSeconds(-1):yyyy-MM-dd}.html");
            await File.WriteAllTextAsync(path, ReportService.Html(from, to.AddSeconds(-1), parts, _kwhPrice, _currency, _extraWatts));
            Log($"Report saved: {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Couldn't save the report — {ex.Message}");
            return null;
        }
    }

    // --- cooling check ---

    /// <summary>
    /// Once a day: the temperature at the same light load this week vs a month ago. A cooler clogging with dust
    /// or a fan slowing down shows as a steady rise; a warmer room does too, which the alert says.
    /// </summary>
    private async Task CheckCooling()
    {
        if (!_alertCoolingOn) return;
        var now = DateTime.Now;
        var (recent, before) = await Task.Run(() => (HistoryStore.ReadMinutes(now.AddDays(-7), now, systemOnly: true).System,
                                                     HistoryStore.ReadMinutes(now.AddDays(-35), now.AddDays(-28), systemOnly: true).System));
        void Compare(string part, Func<SysSample, float> load, Func<SysSample, float> temp)
        {
            static float? Typical(List<SysSample> minutes, Func<SysSample, float> load, Func<SysSample, float> temp)
            {
                var temps = minutes.Where(m => load(m) is >= 5 and <= 40 && !float.IsNaN(temp(m))).Select(temp).OrderBy(t => t).ToList();
                return temps.Count >= 300 ? temps[temps.Count / 2] : null; // at least 5 hours of light use to compare
            }
            if (Typical(recent, load, temp) is not { } nowTemp || Typical(before, load, temp) is not { } thenTemp || nowTemp - thenTemp < CoolingAlertDegrees) return;
            Alert($"cooling:{part}", $"{part} runs {nowTemp - thenTemp:0}°C hotter than a month ago at the same light load ({thenTemp:0}°C then, {nowTemp:0}°C now) — "
                + "dust in the cooler or a slowing fan, unless the room got warmer.");
        }
        Compare("CPU", m => m.Cpu, m => m.CpuTemp);
        Compare("GPU", m => m.Gpu, m => m.GpuTemp);
    }

    private const int CoolingAlertDegrees = 7;

    // --- phone page ---

    private void Phone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneCheck.IsChecked == true) StartPhone();
        else StopPhone();
    }

    private async void PhonePassword_Click(object sender, RoutedEventArgs e)
    {
        string password = PhonePasswordBox.Password;
        if (password.Length < 8) { PhoneStatus.Text = "The page's password needs at least 8 characters."; return; }
        PhonePasswordBox.Clear();
        var salt = RemoteCrypto.NewSalt();
        var hash = await Task.Run(() => RemoteCrypto.FromPassword(password, salt));
        using (var key = Registry.CurrentUser.CreateSubKey(PhoneKey))
        {
            key.SetValue("Salt", RemoteCrypto.Base64Url(salt));
            key.SetValue("Hash", RemoteCrypto.Base64Url(hash));
        }
        Log("Phone page password set.");
        if (_phone != null) StartPhone();
        else PhoneStatus.Text = "Password set. Tick Allow to turn the page on.";
    }

    private void StartPhone()
    {
        StopPhone(removeRule: false);
        byte[]? hash, salt;
        using (var key = Registry.CurrentUser.OpenSubKey(PhoneKey))
            (hash, salt) = (key?.GetValue("Hash") is string h ? RemoteCrypto.ParseKey(h) : null, key?.GetValue("Salt") is string s ? RemoteCrypto.ParseSalt(s) : null);
        if (hash == null || salt == null) { PhoneCheck.IsChecked = false; PhoneStatus.Text = "Set the page's password first."; return; }
        if (!int.TryParse(PhonePortBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            PhoneCheck.IsChecked = false;
            PhoneStatus.Text = "The port must be a whole number from 1024 to 65535.";
            return;
        }
        try { _phone = new PhoneServer(port, hash, salt, PhoneCards); }
        catch (HttpListenerException ex) { PhoneCheck.IsChecked = false; PhoneStatus.Text = $"Couldn't listen on port {port} — {ex.Message}"; return; }
        Netsh($"advfirewall firewall delete rule name=\"{PhoneFirewallRule}\"");
        Netsh($"advfirewall firewall add rule name=\"{PhoneFirewallRule}\" dir=in action=allow protocol=TCP localport={port} profile=private");
        SavePhoneSettings(on: true, port);
        var addresses = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => $"http://{a}:{port}/");
        PhoneStatus.Text = $"On. Open {string.Join(" or ", addresses)} on a phone on this network (any user name, the page's password).";
    }

    private void StopPhone(bool removeRule = true)
    {
        _phone?.Dispose();
        _phone = null;
        if (!removeRule) return;
        Netsh($"advfirewall firewall delete rule name=\"{PhoneFirewallRule}\"");
        SavePhoneSettings(on: false, int.TryParse(PhonePortBox.Text, out int port) ? port : DefaultPhonePort);
        PhoneStatus.Text = "Off.";
    }

    private static void SavePhoneSettings(bool on, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PhoneKey);
        key.SetValue("On", on ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Port", port, RegistryValueKind.DWord);
    }

    /// <summary>The cards for the phone page (runs on the page's thread, so it asks the UI thread).</summary>
    private byte[] PhoneCards() => Dispatcher.Invoke(() => JsonSerializer.SerializeToUtf8Bytes(
        new[] { new { name = $"{Environment.MachineName} (this PC)", online = true, status = _localSnap is { } s ? CardStatus(s) : "Starting…" } }
            .Concat(_pcCards.Select(c => new { name = c.Name, online = c.Online, status = c.Status }))));
}
