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
    };

    private int _alertCpuTemp, _alertGpuTemp, _alertMem, _alertAppCpu, _alertAppMinutes;
    private bool _alertCpuTempOn, _alertGpuTempOn, _alertMemOn, _alertAppCpuOn, _alertUnsignedOn;
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

        (_alertCpuTemp, _alertGpuTemp, _alertMem, _alertAppCpu, _alertAppMinutes) =
            (values["CpuTemp"], values["GpuTemp"], values["Memory"], values["AppCpu"], values["AppMinutes"]);
        (_alertCpuTempOn, _alertGpuTempOn, _alertMemOn, _alertAppCpuOn, _alertUnsignedOn) =
            (AlertCpuTempCheck.IsChecked == true, AlertGpuTempCheck.IsChecked == true, AlertMemCheck.IsChecked == true,
             AlertAppCpuCheck.IsChecked == true, AlertUnsignedCheck.IsChecked == true);
    }

    private int CurrentAlertValue(string name, int fallback)
    {
        int v = name switch
        {
            "CpuTemp" => _alertCpuTemp, "GpuTemp" => _alertGpuTemp, "Memory" => _alertMem,
            "AppCpu" => _alertAppCpu, _ => _alertAppMinutes,
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
    private readonly Dictionary<string, DateTime> _alertedAt = new();

    /// <summary>Checks the rules against this second's reading (and the app list, when it was sampled).</summary>
    private void CheckAlerts(ResourceSnapshot snap, List<ProcessUsage>? processes)
    {
        var now = DateTime.UtcNow;
        CheckLimits(_localAlerts, snap, pc: null);

        if (processes == null) return;
        if (!_alertAppCpuOn) { _appBusySince.Clear(); return; }
        var busy = processes.Where(p => p.Cpu >= _alertAppCpu).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _appBusySince.Keys.Where(n => !busy.ContainsKey(n)).ToList()) _appBusySince.Remove(name);
        foreach (var (name, p) in busy)
        {
            if (!_appBusySince.TryGetValue(name, out var since)) { _appBusySince[name] = now; continue; }
            if ((now - since).TotalMinutes >= _alertAppMinutes)
                Alert($"app:{name}", $"{name} has used over {_alertAppCpu}% CPU for {_alertAppMinutes} min (now {p.Cpu:0}%)");
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

    /// <summary>Every 5 s: each watched PC with alerts on is asked for its reading and checked against the same limits.</summary>
    private void PollRemoteAlerts()
    {
        foreach (var pc in _remotes.Where(r => RemoteAlertsOn(r.Name)))
        {
            if (!_remoteAlertPolling.Add(pc.Name)) continue; // still waiting on the last answer
            var state = _remoteAlerts.TryGetValue(pc.Name, out var s) ? s : _remoteAlerts[pc.Name] = new AlertState();
            Task.Run(() => JsonSerializer.Deserialize<ResourceSnapshot>(pc.Live(), LiveJson)).ContinueWith(t =>
            {
                _remoteAlertPolling.Remove(pc.Name);
                if (!_remotes.Contains(pc)) return; // removed meanwhile
                if (t.IsCompletedSuccessfully && t.Result is { } snap)
                {
                    if (state.OfflineAlerted) Alert($"{pc.Name}:online", $"{pc.Name} is answering again");
                    (state.OfflineSince, state.OfflineAlerted) = (null, false);
                    CheckLimits(state, snap, pc.Name);
                    return;
                }
                state.OfflineSince ??= DateTime.UtcNow;
                if (!state.OfflineAlerted && (DateTime.UtcNow - state.OfflineSince.Value).TotalSeconds >= OfflineAfterSeconds)
                {
                    state.OfflineAlerted = true;
                    Alert($"{pc.Name}:offline", $"{pc.Name} stopped answering — {t.Exception?.InnerException?.Message ?? "no reading"}");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

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
        Log($"Alert: {text}");
        try { HistoryStore.WriteEvent("alert", text); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { } // the log line above still has it
        _tray?.ShowBalloonTip(10000, "Sysoptimizer alert", text.Length > 250 ? text[..250] : text, System.Windows.Forms.ToolTipIcon.Warning);
        if (EventsList.IsVisible) _ = LoadEvents();
    }
}
