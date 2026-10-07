using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>Processes, History, Privacy, background recording and the Claude connector.</summary>
public partial class MainWindow
{
    private readonly ProcessMonitor _processMonitor = new();
    private readonly ObservableCollection<ProcessRow> _processRows = new();
    private readonly Dictionary<string, ProcessRow> _processRowMap = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessUsage> _lastProcesses = new();
    private string _processSort = "cpu";
    private readonly ObservableCollection<OptionItem> _blockedItems = new();
    private long _tick;
    private bool _historyWriteFailed, _pawnIoHintShown;

    // Ending these takes Windows down with them (or simply fails); Task Manager warns, we just refuse.
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "csrss", "wininit", "winlogon", "smss", "lsass", "services", "svchost", "dwm", "fontdrvhost", "Registry", "Memory Compression", "Sysoptimizer" };

    private void InitMonitoring()
    {
        ProcessesList.ItemsSource = _processRows;
        BlockedList.ItemsSource = _blockedItems;
        RefreshBlocked();

        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return; // SelectionChanged bubbles up from every ComboBox inside the tabs too
            if (Tabs.SelectedItem is System.Windows.Controls.TabItem { Header: "History" }) { _animateChart = true; _ = LoadHistory(); }
        };
        PrivacyList.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) RefreshPrivacy(); }; // a pill under Processes
        InitHistory();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Recording is always on: Sysoptimizer starts with Windows into the tray, and closing the window only
        // hides it there. Exit in the tray menu stops it; an update's Application.Shutdown closes it too.
        SetupTray();
        // Point the logon task at this exe on every start: an install or update may have moved it since.
        // Never from a Debug build: a test run would capture the task, and logon (and the updater's
        // restart) would then keep launching that stale dev exe instead of the installed one.
#if !DEBUG
        Task.Run(() => { if (BackgroundMode.Register() is { } error) Dispatcher.Invoke(() => Log($"Couldn't set up starting with Windows — {error}")); });
#endif
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; // keep recording; the tray icon brings it back
            Hide();
        };
        Closed += (_, _) => _tray?.Dispose();
    }

    /// <summary>Runs on every 1-second resource tick: records the history and feeds the live tabs.</summary>
    private HashSet<string>? _runningApps;

    private async Task OnResourceTick(ResourceSnapshot snap)
    {
        _tick++;
        // Apps every 10 s normally, every 2 s while CPU or GPU is pegged — a 15-second burst then gets
        // 7 snapshots instead of 1, enough to see exactly which app did it.
        bool spike = snap.CpuPercent >= 80 || (snap.GpuAvailable && snap.GpuPercent >= 80);
        bool historyTick = _tick % 10 == 1 || (spike && _tick % 2 == 0);
        bool processesShown = ProcessesList.IsVisible;
        List<ProcessUsage>? processes = null;
        if (historyTick || (processesShown && _tick % 2 == 0))
            processes = _lastProcesses = await Task.Run(() => _processMonitor.Sample(snap.GpuByPid));

        // The first tick only primes the baselines (CPU% is a delta between two readings, so everything
        // reads 0 on it) — recording it would drag every average and minimum down.
        if (_tick == 1) return;

        var now = DateTime.UtcNow;
        var sample = new SysSample(now, (float)snap.CpuPercent, (float)snap.MemPercent,
            snap.GpuAvailable ? (float)snap.GpuPercent : float.NaN, (float)snap.CpuTempC, (float)snap.GpuTempC);
        var top = historyTick && processes != null
            ? ProcessMonitor.ForHistory(processes).Select(p => new ProcSample(now, p.Name, (float)p.Cpu, (float)p.RamMB, (float)p.Gpu)).ToList()
            : null;
        try
        {
            if (_tick == 2) HistoryStore.WriteEvent("start", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000).ToString());
            HistoryStore.Write(sample, top);
            if (processes != null) RecordLaunches(processes);
        }
        catch (Exception ex)
        {
            if (!_historyWriteFailed) Log($"History recording failed — {ex.Message}");
            _historyWriteFailed = true;
        }

        if (processesShown && processes != null) UpdateProcessRows();
        if (PrivacyList.IsVisible && _tick % 5 == 0) RefreshPrivacy();
        if (HistoryChart.IsVisible && _historyEnd == null && !_navDragging && !_chartSelecting && _pan == null && _tick % 10 == 0) _ = LoadHistory();

        if (_tick == 15 && !_pawnIoHintShown)
        {
            _pawnIoHintShown = true;
            if (_resourceMonitor?.SensorError is { } error) Log($"Temperatures unavailable — {error}");
            else if (double.IsNaN(snap.CpuTempC)) Log("CPU temperature needs the PawnIO driver — install it with: winget install namazso.PawnIO");
        }
    }

    private Dictionary<string, DateTime> _newApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Logs each app that appeared since the last process sample. Only apps outside the Windows folder
    /// (svchost, conhost, RuntimeBroker and friends start and stop all day), and only once an app is still
    /// there at the next sample — a command-line tool that lives for a second isn't a launch anyone means.
    /// </summary>
    private void RecordLaunches(List<ProcessUsage> processes)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        var apps = processes.Where(p => p.Path != null && !p.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, firstSeen) in _newApps)
            if (apps.Contains(name)) HistoryStore.WriteEvent("launch", name, firstSeen);
        _newApps = _runningApps == null
            ? new(StringComparer.OrdinalIgnoreCase)
            : apps.Where(a => !_runningApps.Contains(a)).ToDictionary(a => a, _ => DateTime.UtcNow, StringComparer.OrdinalIgnoreCase);
        _runningApps = apps;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) HistoryStore.WriteEvent("sleep");
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) HistoryStore.WriteEvent("wake");
        }
        catch { } // a missed sleep marker only makes that gap draw as "not recording"
    }

    // --- Processes ---

    private void UpdateProcessRows()
    {
        string filter = ProcessFilterBox.Text.Trim();
        Func<ProcessUsage, double> key = _processSort switch { "ram" => p => p.RamMB, "gpu" => p => p.Gpu, _ => p => p.Cpu };
        var sorted = _lastProcesses
            .Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (p.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(key).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var gone in _processRowMap.Keys.Except(_lastProcesses.Select(p => p.Name), StringComparer.OrdinalIgnoreCase).ToList())
            _processRowMap.Remove(gone);

        var rows = sorted.Select(p =>
        {
            if (!_processRowMap.TryGetValue(p.Name, out var row)) _processRowMap[p.Name] = row = new ProcessRow { Name = p.Name };
            row.Update(p);
            return row;
        }).ToList();

        var wanted = rows.ToHashSet();
        for (int i = _processRows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_processRows[i])) _processRows.RemoveAt(i);
        // Move rather than rebuild: containers (and their checkbox state) are reused, nothing flickers.
        for (int i = 0; i < rows.Count; i++)
        {
            int current = _processRows.IndexOf(rows[i]);
            if (current < 0) _processRows.Insert(i, rows[i]);
            else if (current != i) _processRows.Move(current, i);
        }
        ProcessCountText.Text = $"APP · {_lastProcesses.Count} RUNNING";
    }

    private void ProcessSort_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string sort }) _processSort = sort;
        if (IsLoaded) UpdateProcessRows(); // the default pill fires during InitializeComponent, before the list exists
    }

    private void ProcessFilter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded) UpdateProcessRows();
    }

    private void EndTask_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _processRows.Where(r => r.IsChecked).ToList())
        {
            if (CriticalProcesses.Contains(row.Name)) { Log($"{row.Name}: not ended — Windows needs it running."); continue; }
            int ended = 0, failed = 0;
            foreach (var p in Process.GetProcessesByName(row.Name))
            {
                using (p)
                {
                    try { p.Kill(); ended++; }
                    catch { failed++; }
                }
            }
            row.IsChecked = false;
            Log($"{row.Name}: {ended} ended" + (failed > 0 ? $", {failed} refused (protected)." : "."));
        }
    }

    private void BlockApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _processRows.Where(r => r.IsChecked).ToList())
        {
            row.IsChecked = false;
            if (row.Usage.Path is not { } path) { Log($"{row.Name}: can't block — Windows won't say where its exe is."); continue; }
            string? refused = AppBlocker.Block(path);
            Log(refused == null
                ? $"{Path.GetFileName(path)}: blocked — it won't start again until you unblock it. End task to stop the copy running now."
                : $"{row.Name}: not blocked — {refused}.");
        }
        RefreshBlocked();
        ProcessesList.Items.Refresh();
    }

    private void UnblockApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _blockedItems.Where(i => i.IsChecked).ToList())
        {
            AppBlocker.Unblock(item.Name);
            Log($"{item.Name}: unblocked.");
        }
        RefreshBlocked();
    }

    private void RefreshBlocked()
    {
        _blockedItems.Clear();
        foreach (var exe in AppBlocker.GetBlocked()) _blockedItems.Add(new OptionItem { Name = exe });
        BlockedPanel.Visibility = UnblockButton.Visibility = _blockedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Privacy ---

    private void RefreshPrivacy()
    {
        var items = PrivacyService.GetAccessLog().Select(a => new OptionItem
        {
            Name = a.App,
            Description = $"{a.Capability} · {a.Id}",
            Note = a.InUse ? $"{a.Capability.ToUpperInvariant()} IN USE NOW" : Ago(a.LastStop ?? a.LastStart!.Value),
        }).ToList();
        PrivacyList.ItemsSource = items.Count > 0 ? items : new List<OptionItem> { new() { Name = "Nothing yet", Description = "No app has used the camera, microphone or location." } };
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalMinutes < 1 ? "just now"
             : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min ago"
             : span.TotalDays < 1 ? $"{span.TotalHours:0} h ago"
             : when.ToString("d MMM yyyy, HH:mm");
    }

    // --- Background / tray ---

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting;

    private void SetupTray()
    {
        if (_tray != null) return;
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Sysoptimizer", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit (stops recording)", null, (_, _) => ExitApp());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize),
            Text = "Sysoptimizer — recording",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    public void ShowFromTray()
    {
        bool wasHidden = !IsVisible;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (wasHidden) _ = CheckForUpdate(manual: false); // an update announced in the tray opens its dialog now
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // --- Claude connector (MCP) ---

    private void ConnectClaude_Click(object sender, RoutedEventArgs e)
    {
        string server = Path.Combine(AppContext.BaseDirectory, "sysoptimizer-mcp.exe");
        if (!File.Exists(server)) { Log("sysoptimizer-mcp.exe isn't next to Sysoptimizer — install with the setup to use the Claude connector."); return; }

        foreach (string configDir in ClaudeDesktopConfigDirs())
        {
            string config = Path.Combine(configDir, "claude_desktop_config.json");
            try
            {
                var root = File.Exists(config) ? JsonNode.Parse(File.ReadAllText(config)) as JsonObject : new JsonObject();
                if (root == null) throw new JsonException("not a JSON object");
                if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
                servers["sysoptimizer"] = new JsonObject { ["command"] = server };
                if (File.Exists(config)) File.Copy(config, config + ".bak", overwrite: true);
                File.WriteAllText(config, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Log($"Claude Desktop: Sysoptimizer connector added ({config}). Restart Claude Desktop to load it.");
            }
            catch (Exception ex) { Log($"Claude Desktop: left {config} untouched — {ex.Message}"); }
        }

        System.Windows.Clipboard.SetText($"claude mcp add --scope user sysoptimizer -- \"{server}\"");
        Log("Claude Code: the setup command is on your clipboard — paste it into a terminal.");
    }

    /// <summary>Classic install keeps its config in %APPDATA%\Claude; the Store (MSIX) build virtualizes it under its package.</summary>
    private static IEnumerable<string> ClaudeDesktopConfigDirs()
    {
        string classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (Directory.Exists(classic)) yield return classic;
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.GetDirectories(packages, "Claude_*"))
        {
            string virtualized = Path.Combine(package, "LocalCache", "Roaming", "Claude");
            if (Directory.Exists(virtualized)) yield return virtualized;
        }
    }
}
