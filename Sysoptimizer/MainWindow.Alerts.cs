using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>
/// The Events page (everything the history recorded happening: launches, sleep, starts, alerts) and the
/// alerts themselves — rules checked on every 1-second tick that notify from the tray and log an event.
/// </summary>
public partial class MainWindow
{
    // --- Events ---

    private List<OptionItem> _events = new();
    private bool _eventsLoading;

    private void InitEvents()
    {
        EventsList.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _ = LoadEvents(); };
        LoadAlertSettings();
    }

    private async Task LoadEvents()
    {
        if (_eventsLoading) return;
        _eventsLoading = true;
        try
        {
            double hours = EventsRangeCombo.SelectedItem is ComboBoxItem { Tag: string h } ? double.Parse(h) : 24;
            var to = DateTime.Now;
            var from = to.AddHours(-hours);
            // The minute summaries carry every event, so even 30 days read quickly; today is summarized from the raw file.
            var source = _source; // another PC's events when History is watching one
            var events = await Task.Run(() => source?.ReadMinutes(from, to).Events ?? HistoryStore.ReadMinutes(from, to).Events);
            _events = events.OrderByDescending(e => e.Time).Select(EventItem).ToList();
            FilterEvents();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or RemoteHistoryException)
        {
            Log($"Couldn't read the events — {ex.Message}");
        }
        finally { _eventsLoading = false; }
    }

    private static OptionItem EventItem(HistoryEvent e)
    {
        var local = e.Time.ToLocalTime();
        var (name, description) = e.Kind switch
        {
            "launch" => (e.Detail, "Started"),
            "sleep" => ("PC went to sleep", "Power"),
            "wake" => ("PC woke up", "Power"),
            "start" => ("Sysoptimizer started", long.TryParse(e.Detail, out long boot)
                ? $"PC booted {DateTimeOffset.FromUnixTimeSeconds(boot).LocalDateTime:ddd d MMM HH:mm}" : "Recording"),
            "alert" => (e.Detail, "Alert"),
            _ when ChangeKinds.TryGetValue(e.Kind, out var change) => (e.Detail, change.Label),
            _ => (e.Detail, e.Kind),
        };
        return new OptionItem
        {
            Name = name, Description = description, Tag = e.Time,
            Note = local.Date == DateTime.Today ? local.ToString("HH:mm:ss") : local.ToString("ddd d MMM HH:mm"),
        };
    }

    private void FilterEvents()
    {
        string filter = EventsFilterBox.Text.Trim();
        var shown = filter.Length == 0 ? _events
            : _events.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        EventsList.ItemsSource = shown.Count > 0 ? shown
            : new List<OptionItem> { new() { Name = filter.Length > 0 ? "Nothing matches" : "Nothing recorded in this range", Description = "" } };
    }

    private void EventsRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = LoadEvents();
    }

    private void EventsFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) FilterEvents();
    }

    /// <summary>An event was clicked: History, the hour around it, with the moment pinned.</summary>
    private void EventRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptionItem { Tag: DateTime utc } }) return;
        _pinnedUtc = utc;
        _frozeForPin = false;
        _animateChart = true;
        var end = utc.ToLocalTime().AddMinutes(30);
        _historyEnd = end >= DateTime.Now ? null : end;
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == "History");
        if (HistoryRange1h.IsChecked == true) _ = LoadHistory();
        else HistoryRange1h.IsChecked = true; // its Checked handler reloads
    }

    // --- Alerts ---

    private const string AlertsKey = @"Software\Sysoptimizer\Alerts";
    private const int SustainSeconds = 30, MemSustainSeconds = 60;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(30);

    // Each rule's limits, with the range a typed value must fall in.
    private (CheckBox Check, TextBox Box, string Key, int Default, int Min, int Max)[] AlertFields => new[]
    {
        (AlertCpuTempCheck, AlertCpuTempBox, "CpuTemp", 90, 40, 110),
        (AlertGpuTempCheck, AlertGpuTempBox, "GpuTemp", 85, 40, 110),
        (AlertMemCheck, AlertMemBox, "Memory", 90, 10, 100),
        (AlertAppCpuCheck, AlertAppCpuBox, "AppCpu", 50, 1, 100),
        (AlertAppCpuCheck, AlertAppMinutesBox, "AppMinutes", 5, 1, 240),
        (AlertAppMemCheck, AlertAppMemBox, "AppMemGB", 16, 1, 512),
        (AlertDiskCheck, AlertDiskBox, "DiskFree", 10, 1, 50),
        (AlertBatteryCheck, AlertBatteryBox, "Battery", 15, 5, 90),
        (AlertDeviceBatteryCheck, AlertDeviceBatteryBox, "DeviceBattery", 20, 5, 90),
    };

    private int _alertCpuTemp, _alertGpuTemp, _alertMem, _alertAppCpu, _alertAppMinutes, _alertAppMem, _alertDisk, _alertBattery, _alertDeviceBattery;
    private bool _alertCpuTempOn, _alertGpuTempOn, _alertMemOn, _alertAppCpuOn, _alertUnsignedOn, _alertAppMemOn, _alertDiskOn, _alertBatteryOn, _alertDeviceBatteryOn, _alertCoolingOn, _alertDriveOn;
    private bool _alertSettingsLoading;

    private void LoadAlertSettings()
    {
        _alertSettingsLoading = true;
        using (var key = Registry.CurrentUser.OpenSubKey(AlertsKey))
        {
            foreach (var (check, box, name, def, _, _) in AlertFields)
            {
                box.Text = (key?.GetValue(name) is int v ? v : def).ToString();
                check.IsChecked = key?.GetValue(CheckName(check)) is int on ? on == 1 : true; // all on until switched off
            }
            AlertUnsignedCheck.IsChecked = key?.GetValue(CheckName(AlertUnsignedCheck)) is int u ? u == 1 : true;
            AlertCoolingCheck.IsChecked = key?.GetValue(CheckName(AlertCoolingCheck)) is int c ? c == 1 : true;
            AlertDriveCheck.IsChecked = key?.GetValue(CheckName(AlertDriveCheck)) is int dr ? dr == 1 : true;
        }
        _alertSettingsLoading = false;
        ApplyAlertSettings(save: false);
    }

    private static string CheckName(CheckBox check) => check.Name.Replace("Alert", "").Replace("Check", "") + "On";

    private void AlertSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_alertSettingsLoading) ApplyAlertSettings(save: true);
    }

    /// <summary>Reads the fields; a value that isn't a whole number in range is flagged and keeps the last good one.</summary>
    private void ApplyAlertSettings(bool save)
    {
        using var key = save ? Registry.CurrentUser.CreateSubKey(AlertsKey) : null;
        var values = new Dictionary<string, int>();
        foreach (var (check, box, name, def, min, max) in AlertFields)
        {
            bool valid = int.TryParse(box.Text.Trim(), out int v) && v >= min && v <= max;
            box.ToolTip = valid ? null : $"A whole number from {min} to {max}";
            if (valid) box.ClearValue(Control.BorderBrushProperty);
            else box.SetResourceReference(Control.BorderBrushProperty, "StatusBad");
            values[name] = valid ? v : CurrentAlertValue(name, def);
            if (valid) key?.SetValue(name, v, RegistryValueKind.DWord);
            key?.SetValue(CheckName(check), check.IsChecked == true ? 1 : 0, RegistryValueKind.DWord);
        }
        key?.SetValue(CheckName(AlertUnsignedCheck), AlertUnsignedCheck.IsChecked == true ? 1 : 0, RegistryValueKind.DWord);
        key?.SetValue(CheckName(AlertCoolingCheck), AlertCoolingCheck.IsChecked == true ? 1 : 0, RegistryValueKind.DWord);
        _alertCoolingOn = AlertCoolingCheck.IsChecked == true;
        key?.SetValue(CheckName(AlertDriveCheck), AlertDriveCheck.IsChecked == true ? 1 : 0, RegistryValueKind.DWord);
        _alertDriveOn = AlertDriveCheck.IsChecked == true;

        (_alertCpuTemp, _alertGpuTemp, _alertMem, _alertAppCpu, _alertAppMinutes) =
            (values["CpuTemp"], values["GpuTemp"], values["Memory"], values["AppCpu"], values["AppMinutes"]);
        (_alertCpuTempOn, _alertGpuTempOn, _alertMemOn, _alertAppCpuOn, _alertUnsignedOn) =
            (AlertCpuTempCheck.IsChecked == true, AlertGpuTempCheck.IsChecked == true, AlertMemCheck.IsChecked == true,
             AlertAppCpuCheck.IsChecked == true, AlertUnsignedCheck.IsChecked == true);
        (_alertAppMem, _alertDisk, _alertBattery) = (values["AppMemGB"], values["DiskFree"], values["Battery"]);
        (_alertAppMemOn, _alertDiskOn, _alertBatteryOn) = (AlertAppMemCheck.IsChecked == true, AlertDiskCheck.IsChecked == true, AlertBatteryCheck.IsChecked == true);
        (_alertDeviceBattery, _alertDeviceBatteryOn) = (values["DeviceBattery"], AlertDeviceBatteryCheck.IsChecked == true);
    }

    private int CurrentAlertValue(string name, int fallback)
    {
        int v = name switch
        {
            "CpuTemp" => _alertCpuTemp, "GpuTemp" => _alertGpuTemp, "Memory" => _alertMem,
            "AppCpu" => _alertAppCpu, "AppMinutes" => _alertAppMinutes, "AppMemGB" => _alertAppMem,
            "DiskFree" => _alertDisk, "DeviceBattery" => _alertDeviceBattery, _ => _alertBattery,
        };
        return v > 0 ? v : fallback;
    }

    /// <summary>Since when each limit has been crossed, for one PC (this one, or each watched one).</summary>
    private sealed class AlertState
    {
        public DateTime? CpuHotSince, GpuHotSince, MemHighSince, OfflineSince;
        public bool OfflineAlerted;
    }

    private readonly AlertState _localAlerts = new();
    private readonly Dictionary<string, AlertState> _remoteAlerts = new();
    private readonly Dictionary<string, DateTime> _appBusySince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _appBigSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _alertedAt = new();

    /// <summary>Checks the rules against this second's reading (and the app list, when it was sampled).</summary>
    private void CheckAlerts(ResourceSnapshot snap, List<ProcessUsage>? processes)
    {
        CheckLimits(_localAlerts, snap, pc: null);
        ShowTrayBatteries(snap.Peripherals);

        if (processes == null) return;
        Sustained(_appBusySince, _alertAppCpuOn ? processes.Where(p => p.Cpu >= _alertAppCpu) : [], TimeSpan.FromMinutes(_alertAppMinutes),
            p => ($"app:{p.Name}", $"{p.Name} has used over {_alertAppCpu}% CPU for {_alertAppMinutes} min (now {p.Cpu:0}%)"));
        Sustained(_appBigSince, _alertAppMemOn ? processes.Where(p => p.RamMB >= _alertAppMem * 1024.0) : [], TimeSpan.FromSeconds(MemSustainSeconds),
            p => ($"app-mem:{p.Name}", $"{p.Name} has used over {_alertAppMem} GB of memory for a minute (now {p.RamMB / 1024:0.0} GB)"));

        // Alerts once an app has stayed over the limit for the whole span (an app that dips under starts over).
        void Sustained(Dictionary<string, DateTime> since, IEnumerable<ProcessUsage> over, TimeSpan span, Func<ProcessUsage, (string Key, string Text)> alert)
        {
            var now = DateTime.UtcNow;
            var current = over.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in since.Keys.Where(n => !current.ContainsKey(n)).ToList()) since.Remove(name);
            foreach (var (name, p) in current)
            {
                if (!since.TryGetValue(name, out var start)) { since[name] = now; continue; }
                if (now - start >= span) { var (key, text) = alert(p); Alert(key, text); }
            }
        }
    }

    /// <summary>The temperature and memory rules, for this PC (pc = null) or a watched one (its name leads the text).</summary>
    private void CheckLimits(AlertState state, ResourceSnapshot snap, string? pc)
    {
        var now = DateTime.UtcNow;
        string who = pc == null ? "" : $"{pc}: ", key = pc == null ? "" : $"{pc}:";
        Sustained(ref state.CpuHotSince, _alertCpuTempOn && snap.CpuTempC >= _alertCpuTemp, SustainSeconds, key + "cpu-temp",
            () => $"{who}CPU at {snap.CpuTempC:0}°C for over {SustainSeconds} s (limit {_alertCpuTemp}°C)");
        Sustained(ref state.GpuHotSince, _alertGpuTempOn && snap.GpuTempC >= _alertGpuTemp, SustainSeconds, key + "gpu-temp",
            () => $"{who}GPU at {snap.GpuTempC:0}°C for over {SustainSeconds} s (limit {_alertGpuTemp}°C)");
        Sustained(ref state.MemHighSince, _alertMemOn && snap.MemPercent >= _alertMem, MemSustainSeconds, key + "memory",
            () => $"{who}Memory at {snap.MemPercent:0}% for over a minute (limit {_alertMem}%)");
        if (_alertBatteryOn && snap.Battery is { PluggedIn: false } battery && battery.Percent <= _alertBattery)
            Alert(key + "battery", $"{who}Battery at {battery.Percent:0}% and not plugged in (limit {_alertBattery}%)");
        if (_alertDeviceBatteryOn)
            foreach (var device in snap.Peripherals.Where(p => p.IsLow(_alertDeviceBattery)))
                Alert(key + "device-battery:" + device.Name, $"{who}{device.Name}: battery at {device.Shown.ToLowerInvariant()} (limit {_alertDeviceBattery}%)");
        if (_alertDriveOn)
            foreach (var drive in snap.DriveHealth.Where(d => DriveStatus(d).Level != "ok"))
                Alert(key + "drive:" + drive.Name, $"{who}{drive.Name}: {DriveStatus(drive).Text.ToLowerInvariant()} — {DriveDetails(drive)}");
        if (_alertDiskOn)
            foreach (var drive in snap.Drives.Where(d => d.TotalGB > 0 && d.FreeGB / d.TotalGB * 100 <= _alertDisk))
                Alert(key + "disk:" + drive.Name, $"{who}{drive.Name} is almost full — {drive.FreeGB:0.#} GB free of {drive.TotalGB:0} GB (limit {_alertDisk}% free)");

        void Sustained(ref DateTime? since, bool over, int seconds, string alertKey, Func<string> text)
        {
            if (!over) { since = null; return; }
            since ??= now;
            if ((now - since.Value).TotalSeconds >= seconds) Alert(alertKey, text());
        }
    }

    // --- watched PCs ---

    private const string RemoteAlertsKey = @"Software\Sysoptimizer\RemotePCAlerts";
    private const int OfflineAfterSeconds = 60;
    private readonly HashSet<string> _remoteAlertPolling = new();

    private static bool RemoteAlertsOn(string pc)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RemoteAlertsKey);
        return key?.GetValue(pc) is not 0; // on unless switched off
    }

    /// <summary>
    /// Every 5 s: each watched PC is asked for its reading — for its card on the Resources tab, and, with its
    /// alerts on, checked against the same limits as this PC.
    /// </summary>
    private void PollRemotes()
    {
        foreach (var pc in _remotes.ToList())
        {
            if (!_remoteAlertPolling.Add(pc.Name)) continue; // still waiting on the last answer
            var state = _remoteAlerts.TryGetValue(pc.Name, out var s) ? s : _remoteAlerts[pc.Name] = new AlertState();
            bool askIdentity = DateTime.UtcNow - pc.IdentityAt > TimeSpan.FromMinutes(10);
            Task.Run(() =>
            {
                var snap = JsonSerializer.Deserialize<ResourceSnapshot>(pc.Live(), LiveJson);
                return (Snap: snap, Identity: askIdentity ? pc.Identity() : ((string, string[])?)null);
            }).ContinueWith(t =>
            {
                _remoteAlertPolling.Remove(pc.Name);
                if (!_remotes.Contains(pc)) return; // removed meanwhile
                var card = _pcCards.FirstOrDefault(c => c.Name == pc.Name);
                bool alerts = RemoteAlertsOn(pc.Name);
                if (t.IsCompletedSuccessfully && t.Result.Snap is { } snap)
                {
                    if (t.Result.Identity is (var version, var macs))
                    {
                        pc.IdentityAt = DateTime.UtcNow;
                        pc.Version = version;
                        if (card != null) card.Version = version.Length > 0 ? $"v{version}" : "older than 1.9";
                        if (macs.Length > 0 && !macs.SequenceEqual(pc.Macs)) // remembered, to wake it once it's off
                        {
                            pc.Macs = macs;
                            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RemotePcsKey);
                            key.SetValue(pc.Name, pc.Saved);
                        }
                    }
                    if (card != null) (card.Online, card.Status) = (true, CardStatus(snap));
                    if (state.OfflineAlerted && alerts) Alert($"{pc.Name}:online", $"{pc.Name} is answering again");
                    (state.OfflineSince, state.OfflineAlerted) = (null, false);
                    if (alerts) CheckLimits(state, snap, pc.Name);
                    return;
                }
                string reason = t.Exception?.InnerException?.Message ?? "no reading";
                if (card != null) (card.Online, card.Status) = (false, $"Offline — {reason}");
                state.OfflineSince ??= DateTime.UtcNow;
                if (alerts && !state.OfflineAlerted && (DateTime.UtcNow - state.OfflineSince.Value).TotalSeconds >= OfflineAfterSeconds)
                {
                    state.OfflineAlerted = true;
                    Alert($"{pc.Name}:offline", $"{pc.Name} stopped answering — {reason}");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // One reading per line, always in the same order, so every card has the same shape and none jumps as the numbers change.
    private static string CardStatus(ResourceSnapshot s) => string.Join("\n", new[]
    {
        $"CPU {s.CpuPercent:0}%" + (double.IsNaN(s.CpuTempC) ? "" : $" · {s.CpuTempC:0}°C"),
        $"RAM {s.MemPercent:0}%",
        s.GpuAvailable ? $"GPU {s.GpuPercent:0}%" + (double.IsNaN(s.GpuTempC) ? "" : $" · {s.GpuTempC:0}°C") : null,
        s.Drives.Count > 0 ? $"Disk {s.Drives.Min(d => d.TotalGB > 0 ? d.FreeGB / d.TotalGB * 100 : 100):0}% free (fullest drive)" : null,
        s.Battery is { } b ? $"Battery {b.Percent:0}%{(b.PluggedIn ? "" : " (unplugged)")}" : null,
        double.IsNaN(s.Fps) ? null : $"{s.Fps:0} FPS in {s.FpsApp}",
    }.Where(x => x != null));

    private void RemoteAlerts_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string pc } check) return;
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RemoteAlertsKey))
            key.SetValue(pc, check.IsChecked == true ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        _remoteAlerts.Remove(pc); // start the timers afresh
    }

    /// <summary>Called for each confirmed launch; checks the exe's signature off the UI thread.</summary>
    private void CheckLaunchSignature(string name, string? path)
    {
        if (!_alertUnsignedOn || path == null) return;
        Task.Run(() => AppIdentity.SignatureOf(path)).ContinueWith(t =>
        {
            if (t.Result != SignatureState.Signed)
                Alert($"unsigned:{path}", $"{name} started and is {(t.Result == SignatureState.Unsigned ? "not signed" : "signed with a signature Windows doesn't trust")} — {path}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Notifies once per subject per half hour: a tray balloon, the log, and an event in the history.</summary>
    private void Alert(string key, string text)
    {
        var now = DateTime.UtcNow;
        if (_alertedAt.TryGetValue(key, out var last) && now - last < AlertCooldown) return;
        _alertedAt[key] = now;
        _balloonOpens = null; // a click on this balloon opens the window, not an earlier report
        Log($"Alert: {text}");
        try { HistoryStore.WriteEvent("alert", text); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { } // the log line above still has it
        _tray?.ShowBalloonTip(10000, "Sysoptimizer alert", text.Length > 250 ? text[..250] : text, System.Windows.Forms.ToolTipIcon.Warning);
        if (EventsList.IsVisible) _ = LoadEvents();
    }
}
