using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<OptionItem> _cleanupItems = new();
    private readonly ObservableCollection<OptionItem> _serviceItems = new();
    private readonly ObservableCollection<OptionItem> _tweakItems = new();
    private readonly ObservableCollection<OptionItem> _appItems = new();
    private readonly ObservableCollection<OptionItem> _startupItems = new();
    private readonly ObservableCollection<OptionItem> _bloatwareItems = new();
    private readonly ObservableCollection<DuplicateGroupView> _duplicateGroups = new();
    private readonly ObservableCollection<OptionItem> _largeFileItems = new();
    private readonly ObservableCollection<OptionItem> _installedAppItems = new();
    private readonly ObservableCollection<OptionItem> _leftoverItems = new();
    private readonly List<Tweak> _tweaks = TweakService.GetTweaks();

    private class DuplicateGroupView
    {
        public required string Header;
        public required ObservableCollection<OptionItem> Files;
    }

    private ResourceMonitor? _resourceMonitor;
    private DispatcherTimer? _resourceTimer;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private UpdateDialog? _updateDialog;
    private string? _updateNotified;
    private readonly Queue<(DateTime Time, double Value)> _cpuHistory = new();
    private readonly Queue<(DateTime Time, double Value)> _memHistory = new();
    private readonly Queue<(DateTime Time, double Value)> _gpuHistory = new();
    private readonly List<Queue<(DateTime Time, double Value)>> _cpuThreadHistory = new();
    private readonly List<System.Windows.Controls.TextBlock> _cpuThreadTexts = new();
    private int _historyWindowSeconds = 60;
    private const int MaxHistorySeconds = 300;

    private readonly Dictionary<string, MetricCard> _diskCards = new();
    private readonly Dictionary<string, MetricCard> _netCards = new();

    private class GraphBinding
    {
        public required FrameworkElement Container;
        public required System.Windows.Shapes.Path Line;
        public required System.Windows.Shapes.Path Fill;
        public required Queue<(DateTime Time, double Value)> History;
        public required bool AutoScale;
        public bool ShowAxis = true;
        public double AxisTopInset; // keeps the top label clear of a top-right control (the CPU card's "Per thread" checkbox)
        public double DisplayValue = double.NaN; // eased towards the latest real sample — see DrawSparkline
        // Subtle horizontal gridlines with their value to the left, built lazily on first draw — see UpdateAxisGridlines.
        public System.Windows.Shapes.Line[]? GridLines;
        public System.Windows.Controls.TextBlock[]? GridLabels;
        public System.Windows.Shapes.Ellipse? Dot;           // the live end of the curve
        public LinearGradientBrush? FadeIn;                    // softens where the recorded data begins
    }
    private static readonly double[] AxisFractions = { 0.0, 0.5, 1.0 }; // fraction of card height, top to bottom
    private readonly List<GraphBinding> _liveGraphs = new();
    private DateTime _lastRender = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        AboutVersionText.Text = $"Version {Updater.Current}";
        SourceInitialized += (_, _) => WindowGlass.EnableAcrylic(this);
        Closed += (_, _) => { _resourceTimer?.Stop(); _resourceMonitor?.Dispose(); CompositionTarget.Rendering -= OnRendering; ThemeManager.ThemeChanged -= OnThemeChanged; _updateTimer.Stop(); };
        ThemeManager.ThemeChanged += OnThemeChanged;
        InitThemePicker();

        _liveGraphs.Add(new GraphBinding { Container = CpuGraphGrid, Line = CpuSparkline, Fill = CpuAreaFill, History = _cpuHistory, AutoScale = false, AxisTopInset = 40 });
        _liveGraphs.Add(new GraphBinding { Container = MemGraphGrid, Line = MemSparkline, Fill = MemAreaFill, History = _memHistory, AutoScale = false, AxisTopInset = 46 });
        _liveGraphs.Add(new GraphBinding { Container = GpuGraphGrid, Line = GpuSparkline, Fill = GpuAreaFill, History = _gpuHistory, AutoScale = false, AxisTopInset = 6 });
        // The live graphs redraw every frame — but only while one can be seen: subscribed, Rendering keeps WPF
        // drawing 60 frames a second even with the window hidden in the tray, where it lives most of the day.
        IsVisibleChanged += (_, _) => UpdateRenderingHook();
        StateChanged += (_, _) => UpdateRenderingHook();
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) { UpdateRenderingHook(); if (Tabs.SelectedItem == AboutTab) RefreshWorksWith(); } };

        ApplyRoundedClip(CpuCardBorder);
        ApplyRoundedClip(MemCardBorder);
        ApplyRoundedClip(GpuCardBorder);
        ApplyRoundedClip(HistoryChartBorder);
        ApplyRoundedClip(HistoryNavigatorBorder);
        CpuThreadsGrid.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutCpuThreads(); };

        InitMonitoring();
        StartResourceMonitor();

        CleanupList.ItemsSource = _cleanupItems;
        ServicesList.ItemsSource = _serviceItems;
        TweaksList.ItemsSource = _tweakItems;
        AppsList.ItemsSource = _appItems;
        StartupList.ItemsSource = _startupItems;
        // Both pages follow the PC being shown, and are read afresh each time they open.
        StartupList.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _ = LoadStartup(); };
        ServicesList.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _ = LoadServices(); };
        BloatwareList.ItemsSource = _bloatwareItems;

        foreach (var t in CleanupService.Targets)
            _cleanupItems.Add(new OptionItem { Name = t.Name, Description = t.Description, Tag = t.Path });

        foreach (var s in ServiceManager.KnownServices)
            _serviceItems.Add(new OptionItem { Name = s.Name, Description = s.Description, Note = "Checking...", Tag = s.Key });

        foreach (var t in _tweaks)
            _tweakItems.Add(new OptionItem { Name = t.Name, Description = t.Description, Note = t.IsApplied() ? "Applied" : "", Tag = t });

        foreach (var a in WingetService.PopularApps)
            _appItems.Add(new OptionItem { Name = a.Name, Tag = a.Id });

        foreach (var item in StartupService.GetItems())
            _startupItems.Add(new OptionItem { Name = item.Name, Description = item.Command, Note = item.Location == "Disabled" ? "Disabled" : "Enabled", Tag = item });

        foreach (var (name, package) in BloatwareService.KnownBloat)
            _bloatwareItems.Add(new OptionItem { Name = name, Note = "Checking...", Tag = package });

        DuplicatesList.ItemsSource = _duplicateGroups;
        LargeFilesList.ItemsSource = _largeFileItems;
        InstalledAppsList.ItemsSource = _installedAppItems;
        LeftoversList.ItemsSource = _leftoverItems;

        foreach (var app in UninstallService.GetInstalledApps())
            _installedAppItems.Add(new OptionItem { Name = app.Name, Description = $"{app.Publisher} {app.Version}".Trim(), Tag = app });

        DnsAdapterCombo.ItemsSource = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => n.Name).ToList();
        if (DnsAdapterCombo.Items.Count > 0) DnsAdapterCombo.SelectedIndex = 0;

        Loaded += async (_, _) => await LoadStatusesAsync();
        StartUpdateChecks();
    }

    // Everything here launches external processes (sc, powershell, winget) — run after the window
    // is on screen, in parallel, so a slow machine sees the app at once and the notes fill in.
    private async Task LoadStatusesAsync()
    {
        var services = Task.Run(() => _serviceItems.Select(i => ServiceManager.GetStatus((string)i.Tag!)).ToList());
        var bloat = Task.Run(BloatwareService.GetInstalledPackages);
        var winget = Task.Run(WingetService.IsWingetAvailable);
        var health = RefreshHealth();

        var statuses = await services;
        for (int i = 0; i < statuses.Count; i++) _serviceItems[i].Note = statuses[i];
        ServicesList.Items.Refresh();

        var installed = await bloat;
        foreach (var item in _bloatwareItems) item.Note = installed.Contains((string)item.Tag!) ? "" : "Not installed";
        BloatwareList.Items.Refresh();

        Log("Ready. " + (await winget ? "winget detected." : "winget is missing — install App Installer from the Microsoft Store."));
        await health;
    }

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // --- Works with: the other Protagonist Labs apps on this PC (see Ecosystem) ---

    private static readonly (string Id, string Name)[] PaidApps = { ("shelf", "Shelf"), ("stash", "Stash"), ("frostpane", "Frostpane"), ("reel", "Reel"), ("planner", "Planner"), ("reminder", "Reminder") };

    /// <summary>Re-read each time About opens: an app installed meanwhile shows up, one removed goes. Quiet on purpose — hint text, the names the only links.</summary>
    private void RefreshWorksWith()
    {
        const string utm = "utm_source=sysoptimizer&utm_medium=app&utm_campaign=works-with";
        WorksWithList.Children.Clear();
        if (Ecosystem.Card("shelf")?["exe"]?.ToString() is { } shelf) // Card checked it is a path that exists
            AddWorksWith(Link("Shelf", () => OpenUnelevated(shelf)), " · shows how each of your games ran here (minutes played, average FPS) on its page");
        else
            AddWorksWith(Link("Shelf", () => OpenUrl($"https://protagonistlabs.app/shelf/?{utm}")), " · would show how each of your games ran here (minutes played, average FPS) on its page");

        if (!Ecosystem.BoughtSomething()) return;
        var missing = PaidApps.Where(a => Ecosystem.Card(a.Id) == null).Take(4).ToList();
        if (missing.Count == 0) return;
        var line = AddWorksWith(new Run("Owners of one of our apps get 30% off the others: "), "");
        line.Margin = new Thickness(0, 6, 0, 0);
        foreach (var (id, name) in missing)
        {
            if (id != missing[0].Id) line.Inlines.Add(new Run(" · "));
            line.Inlines.Add(Link(name, () => OpenUrl($"https://protagonistlabs.app/{id}/?code={(id == "shelf" ? "SECOND30SHELF" : "SECOND30")}&{utm}")));
        }
    }

    private System.Windows.Controls.TextBlock AddWorksWith(Inline first, string rest)
    {
        var line = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
        line.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary"); // follows a theme change
        line.Inlines.Add(first);
        if (rest != "") line.Inlines.Add(new Run(rest));
        WorksWithList.Children.Add(line);
        return line;
    }

    /// <summary>A name in the line's own colour, underlined only under the pointer.</summary>
    private static Hyperlink Link(string text, Action open)
    {
        var link = new Hyperlink(new Run(text)) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.Click += (_, _) => open();
        return link;
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Sysoptimizer runs elevated; started through Explorer, the sister app runs as the user, like from Start.</summary>
    private static void OpenUnelevated(string exe)
    {
        try { System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{exe}\""); }
        catch (System.ComponentModel.Win32Exception) { } // gone since the card was read
    }

    // --- Updates ---

    private void StartUpdateChecks()
    {
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) => { first.Stop(); await CheckForUpdate(manual: false); };
        first.Start();
        _updateTimer.Tick += async (_, _) => await CheckForUpdate(manual: false);
        _updateTimer.Start();
    }

    /// <returns>What to tell the user (only used by the manual "Check for updates" link).</returns>
    private async Task<string?> CheckForUpdate(bool manual)
    {
        if (_updateDialog != null) { _updateDialog.Activate(); return null; }
        var release = await Updater.Check(manual);
        if (release == null) return manual ? $"Sysoptimizer {Updater.Current} is the latest version." : null;
        if (!IsVisible)
        {
            // Recording in the tray for days: the dialog needs the window, so say it from the tray instead —
            // once per version — and clicking the notification opens the window with the dialog.
            if (_tray != null && _updateNotified != release.Tag)
            {
                _updateNotified = release.Tag;
                _tray.ShowBalloonTip(15000, $"Sysoptimizer {release.Version} is available",
                    "Click to see what's new and install it.", System.Windows.Forms.ToolTipIcon.Info);
            }
            return null;
        }

        _updateDialog = new UpdateDialog(this, release);
        _updateDialog.Closed += (_, _) => { Updater.Dismiss(release); _updateDialog = null; };
        _updateDialog.Show();
        Log($"Update available: Sysoptimizer {release.Version}.");
        return null;
    }

    // The same check the timer runs, on demand. A new version opens the update dialog; otherwise the
    // answer ("… is the latest version") replaces the link's own text, so nothing pops up for nothing.
    private async void CheckUpdatesLink_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesLink.IsEnabled = false;
        SetLinkText(CheckUpdatesLink, "Checking...");
        var answer = await CheckForUpdate(manual: true);
        CheckUpdatesLink.IsEnabled = true;
        SetLinkText(CheckUpdatesLink, answer ?? "Check for updates");
    }

    private static void SetLinkText(System.Windows.Documents.Hyperlink link, string text)
    {
        link.Inlines.Clear();
        link.Inlines.Add(new System.Windows.Documents.Run(text));
    }

    private void Log(string message)
    {
        AppLog.Write(message);
        string text = LogText.Text + $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        // Days of messages in one TextBlock made each new line slower to add and lay out: the panel keeps the
        // last few hundred lines; the log file keeps them all.
        LogText.Text = text.Length > 40_000 ? text[(text.IndexOf('\n', text.Length - 30_000) + 1)..] : text;
    }

    private static string FormatUptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalDays}:{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // --- Resources ---

    private async void StartResourceMonitor()
    {
        // Setting up the counters takes half a second (and waits for PDH's first tick): off the UI thread,
        // so the window is on screen at once and the graphs start a moment later.
        try { _resourceMonitor = await Task.Run(() => new ResourceMonitor()); }
        catch (Exception ex) { Log($"Resource monitor unavailable — {ex.Message}"); return; }

        _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _resourceTimer.Tick += async (_, _) => await UpdateResources();
        _resourceTimer.Start();
        _ = UpdateResources();
    }

    private bool _sampling;

    private async Task UpdateResources()
    {
        // The timer ticks every second regardless; a slow sample (gateway pings) must not overlap the next
        // one — PDH and the sensor library aren't thread-safe, and the history would get double rows.
        if (_sampling) return;
        _sampling = true;
        try { await UpdateResourcesCore(); }
        finally { _sampling = false; }
    }

    private static string Temp(double c) => double.IsNaN(c) ? "" : $" · {c:0}°C";

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int THREAD_PRIORITY_TIME_CRITICAL = 15; // .NET's ThreadPriority stops at Highest

    private async Task UpdateResourcesCore()
    {
        // Sample() pings each adapter's gateway, which can take up to ~300ms per NIC — keep that off the UI thread.
        // The sample runs at the top non-realtime thread priority: with every core busy (a stress test, a game)
        // the sensor library — which hops onto each core in turn to read it — otherwise queued behind the load
        // on all of them, a sample took 5-10 s, and nothing was recorded until the load stopped. Highest wasn't
        // enough against a load running at High priority. It's a few milliseconds a second.
        var snap = await Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
            try { return _resourceMonitor!.Sample(); }
            finally { thread.Priority = previous; }
        });

        if (_server != null) _liveJson = JsonSerializer.SerializeToUtf8Bytes(snap, LiveJson);
        _localSnap = snap;
        if (_source == null) ShowSnapshot(snap);
        else
        {
            if (!_livePolling) _ = PollRemote(_source);
            if (!_procsPolling && ProcessesList.IsVisible) _ = PollRemoteProcesses(_source);
        }
        await OnResourceTick(snap); // this PC's own reading is always the one recorded and checked for alerts
    }

    private static readonly JsonSerializerOptions LiveJson = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }; // NaN = unknown temperature
    private volatile byte[]? _liveJson; // what this PC's /live answers with
    private bool _livePolling;

    /// <summary>Another PC's reading, fetched alongside (never instead of) this one's, so a slow or gone PC can't stall recording.</summary>
    private async Task PollRemote(RemoteHistory source)
    {
        _livePolling = true;
        try
        {
            var json = await Task.Run(source.Live);
            if (source == _source && JsonSerializer.Deserialize<ResourceSnapshot>(json, LiveJson) is { } remote) ShowSnapshot(remote);
        }
        catch (Exception ex) when (ex is RemoteHistoryException or JsonException)
        {
            if (source == _source) CpuDetailText.Text = $"{source.Name}: {ex.Message}";
        }
        finally { _livePolling = false; }
    }

    private bool _procsPolling;

    /// <summary>The watched PC's app list, for the Processes tab (it samples every 10 s, or every 2 s while its own list is open).</summary>
    private async Task PollRemoteProcesses(RemoteHistory source)
    {
        _procsPolling = true;
        try
        {
            var json = await Task.Run(source.Processes);
            if (source != _source || JsonSerializer.Deserialize<List<ProcessUsage>>(json, LiveJson) is not { } list) return;
            _remoteProcesses = list;
            UpdateProcessRows();
        }
        catch (Exception ex) when (ex is RemoteHistoryException or JsonException)
        {
            if (source == _source) ProcessCountText.Text = $"{source.Name}: {ex.Message}";
        }
        finally { _procsPolling = false; }
    }

    /// <summary>A reading onto the Resources, Disks and Network tabs — this PC's, or the one being watched.</summary>
    private string _driveHealthShown = "";

    /// <summary>The drives' health on the Disks tab — rebuilt only when something in it changed.</summary>
    private void ShowDriveHealth(List<DriveHealth> drives)
    {
        var rows = drives.Select(d => new OptionItem { Name = d.Name, Description = DriveDetails(d), Note = DriveStatus(d).Text, Tag = DriveStatus(d).Level }).ToList();
        string shown = string.Join("|", rows.Select(r => r.Name + r.Description + r.Note));
        if (shown == _driveHealthShown) return;
        _driveHealthShown = shown;
        DriveHealthList.ItemsSource = rows;
        DriveHealthPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DriveDetails(DriveHealth d) => string.Join(" · ", new[]
    {
        double.IsNaN(d.TempC) ? null : $"{d.TempC:0}°C" + (double.IsNaN(d.WarnTempC) ? "" : $" (warns at {d.WarnTempC:0}°C)"),
        double.IsNaN(d.LifePercent) ? null : $"{d.LifePercent:0}% life left",
        double.IsNaN(d.PowerOnHours) ? null : $"{d.PowerOnHours:N0} h powered on",
        double.IsNaN(d.WrittenTB) ? null : $"{d.WrittenTB:0.#} TB written",
        d.WindowsHealth is { } w ? $"Windows: {w}" : null,
    }.Where(x => x != null));

    /// <summary>The drive's state in a word, and how worried to be: ok, warn or bad.</summary>
    internal static (string Text, string Level) DriveStatus(DriveHealth d) =>
        d.WindowsHealth == "Unhealthy" || d.SpareBelowThreshold || d.LifePercent <= 5 ? ("Failing — back it up", "bad")
        : d.WindowsHealth == "Warning" || d.LifePercent <= 10 ? ("Wearing out", "warn")
        : d.TempC >= d.WarnTempC ? ("Running hot", "warn")
        : ("Healthy", "ok");

    private void ShowSnapshot(ResourceSnapshot snap)
    {
        ShowDriveHealth(snap.DriveHealth);
        CpuPercentText.Text = $"{snap.CpuPercent:0}%";
        CpuDetailText.Text = string.Join(" · ", new[]
        {
            snap.CpuFrequencyGHz > 0 ? $"{snap.CpuFrequencyGHz:0.00} GHz" : null,
            double.IsNaN(snap.CpuTempC) ? null : $"{snap.CpuTempC:0}°C",
            double.IsNaN(snap.CpuWatts) ? null : $"{snap.CpuWatts:0} W",
            Fan(snap, gpu: false),
            $"Up {FormatUptime(snap.UptimeSeconds)}",
        }.Where(s => s != null));
        PushHistory(_cpuHistory, snap.CpuPercent);
        for (int i = 0; i < snap.CpuThreads.Count; i++)
        {
            if (i == _cpuThreadHistory.Count) AddCpuThreadGraph();
            PushHistory(_cpuThreadHistory[i], snap.CpuThreads[i]);
            _cpuThreadTexts[i].Text = $"{snap.CpuThreads[i]:0}%";
        }

        MemPercentText.Text = $"{snap.MemPercent:0}%";
        MemDetailText.Text = $"{snap.MemUsedGB:0.#} / {snap.MemTotalGB:0.#} GB";
        PushHistory(_memHistory, snap.MemPercent);

        if (snap.GpuAvailable)
        {
            GpuPercentText.Text = $"{snap.GpuPercent:0}%";
            GpuDetailText.Text = $"{snap.GpuMemUsedGB:0.#} GB VRAM{Temp(snap.GpuTempC)}" + (double.IsNaN(snap.GpuWatts) ? "" : $" · {snap.GpuWatts:0} W")
                + (Fan(snap, gpu: true) is { } gpuFan ? $" · {gpuFan}" : "") + (double.IsNaN(snap.Fps) ? "" : $" · {snap.Fps:0} FPS in {snap.FpsApp}");
            PushHistory(_gpuHistory, snap.GpuPercent);
        }

        ShowPower(snap);

        foreach (var d in snap.Disks)
        {
            var card = GetOrCreateCard(_diskCards, DiskCardsHost, d.Name, "Speed (MB/s)", "Latency (ms)");
            card.SpeedValueText.Text = $"R {d.ReadMBs:0.#} / W {d.WriteMBs:0.#} MB/s";
            card.LatencyValueText.Text = $"{d.LatencyMs:0.#} ms · {d.ActivePercent:0}% active";
            PushHistory(card.SpeedHistory, d.ReadMBs + d.WriteMBs);
            PushHistory(card.LatencyHistory, d.LatencyMs);
        }

        foreach (var n in snap.Nets)
        {
            var card = GetOrCreateCard(_netCards, NetCardsHost, n.Name, "Speed (Mbps)", "Latency (ms)");
            card.SpeedValueText.Text = $"↓ {n.DownMbps:0.#} / ↑ {n.UpMbps:0.#} Mbps" + (n.LinkSpeedMbps > 0 ? $" · {n.LinkSpeedMbps:0} link" : "");
            card.LatencyValueText.Text = n.LatencyAvailable ? $"{n.LatencyMs:0} ms" : "n/a";
            PushHistory(card.SpeedHistory, n.DownMbps + n.UpMbps);
            if (n.LatencyAvailable) PushHistory(card.LatencyHistory, n.LatencyMs);
        }
    }

    /// <summary>The fastest fan of the graphics card, or of the rest of the PC, as "fan 1,200 RPM"; null when none spins.</summary>
    private static string? Fan(ResourceSnapshot snap, bool gpu) =>
        snap.Fans.Where(f => f.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) == gpu && f.Rpm > 0).Select(f => f.Rpm).DefaultIfEmpty().Max() is > 0 and var rpm
            ? $"fan {rpm:N0} RPM" : null;

    private bool _hasBattery;

    /// <summary>The battery on a laptop, beside "Show last" (the CPU and GPU lines carry the watts and fans).</summary>
    private void ShowPower(ResourceSnapshot snap)
    {
        _hasBattery = snap.Battery != null;
        if (_historyApp == null) HistoryMetricBattery.IsEnabled = _hasBattery;
        BatteryText.Visibility = _hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (snap.Battery is { } b)
            BatteryText.Text = $"Battery {b.Percent:0}% · " + (b.Charging ? "charging" : b.PluggedIn ? "plugged in" : "on battery")
                + (double.IsNaN(b.Watts) ? "" : $" · {b.Watts:0.#} W")
                + (!b.PluggedIn && !double.IsNaN(b.MinutesLeft) ? $" · {(int)(b.MinutesLeft / 60)} h {(int)(b.MinutesLeft % 60)} min left" : "");
    }

    /// <summary>Switching PCs: the live graphs and the per-disk / per-adapter cards start over for the new one.</summary>
    private void ResetLiveGraphs()
    {
        foreach (var q in new[] { _cpuHistory, _memHistory, _gpuHistory }) q.Clear();
        _liveGraphs.RemoveAll(g => g.Container != CpuGraphGrid && g.Container != MemGraphGrid && g.Container != GpuGraphGrid);
        _cpuThreadHistory.Clear();
        _cpuThreadTexts.Clear();
        CpuThreadsGrid.Children.Clear();
        _diskCards.Clear();
        _netCards.Clear();
        DiskCardsHost.Items.Clear();
        NetCardsHost.Items.Clear();
    }

    // Always sampled, so ticking "Per thread" shows the full history straight away. No glow on these:
    // an Effect per mini graph, re-rendered every frame, costs far more than the curves themselves.
    private void AddCpuThreadGraph()
    {
        var (graph, line, fill) = MetricCard.BuildGraph("AccentCpu", "AreaFillCpu", glowKey: null);
        graph.Margin = new Thickness(0, 20, 0, 0); // below the header, so the curve never runs through the text
        var history = new Queue<(DateTime Time, double Value)>();
        _cpuThreadHistory.Add(history);

        var name = new System.Windows.Controls.TextBlock { Text = $"CPU {_cpuThreadHistory.Count - 1}", FontSize = 10 };
        name.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
        var value = new System.Windows.Controls.TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        _cpuThreadTexts.Add(value);
        var header = new System.Windows.Controls.DockPanel { Margin = new Thickness(6, 3, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Controls.DockPanel.SetDock(value, System.Windows.Controls.Dock.Right);
        header.Children.Add(value);
        header.Children.Add(name);

        var content = new System.Windows.Controls.Grid();
        content.Children.Add(graph);
        content.Children.Add(header);
        var cell = new System.Windows.Controls.Border { Child = content, Height = 76, Margin = new Thickness(3), BorderThickness = new Thickness(1), ClipToBounds = true };
        cell.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Border");
        cell.SetResourceReference(System.Windows.Controls.Border.CornerRadiusProperty, "ControlRadius");
        CpuThreadsGrid.Children.Add(cell);
        LayoutCpuThreads();
        _liveGraphs.Add(new GraphBinding { Container = graph, Line = line, Fill = fill, History = history, AutoScale = false, ShowAxis = false });
    }

    private void LayoutCpuThreads() =>
        CpuThreadsGrid.Columns = ThreadColumns(CpuThreadsGrid.Children.Count, (int)(CpuThreadsGrid.ActualWidth / 150));

    /// <summary>
    /// As many ~150px cells per row as fit, but only a count that divides the threads evenly, so every row
    /// is full (8 → 4×2, not 6+2). When nothing divides well (a prime count), balance the rows instead.
    /// </summary>
    internal static int ThreadColumns(int threads, int fit)
    {
        if (threads <= 0) return 1;
        int maxCols = Math.Clamp(fit, 1, threads);
        int cols = Enumerable.Range(1, maxCols).Last(c => threads % c == 0);
        if (cols * 2 > maxCols) return cols;
        int rows = (threads + maxCols - 1) / maxCols;
        return (threads + rows - 1) / rows;
    }

    private void CpuPerThread_Changed(object sender, RoutedEventArgs e)
    {
        bool perThread = CpuPerThreadCheck.IsChecked == true;
        CpuThreadsGrid.Visibility = perThread ? Visibility.Visible : Visibility.Collapsed;
        CpuSparkline.Visibility = CpuAreaFill.Visibility = perThread ? Visibility.Collapsed : Visibility.Visible;
        CpuCardBorder.Height = perThread ? double.NaN : 150;
    }

    /// <summary>Disks and network adapters are discovered at runtime, so their cards are built lazily on first sight.</summary>
    private MetricCard GetOrCreateCard(Dictionary<string, MetricCard> cards, System.Windows.Controls.ItemsControl host, string name, string speedLabel, string latencyLabel)
    {
        if (cards.TryGetValue(name, out var existing)) return existing;

        var card = MetricCard.Build(name, speedLabel, latencyLabel);
        cards[name] = card;
        host.Items.Add(card.Root);
        _liveGraphs.Add(new GraphBinding { Container = card.SpeedGraph, Line = card.SpeedLine, Fill = card.SpeedFill, History = card.SpeedHistory, AutoScale = true });
        _liveGraphs.Add(new GraphBinding { Container = card.LatencyGraph, Line = card.LatencyLine, Fill = card.LatencyFill, History = card.LatencyHistory, AutoScale = true });
        return card;
    }

    private void DurationPill_Checked(object sender, RoutedEventArgs e)
    {
        if (int.TryParse((string)((System.Windows.Controls.RadioButton)sender).Tag, out int seconds))
            _historyWindowSeconds = seconds; // the render loop picks this up on its next frame

    }

    private static void PushHistory(Queue<(DateTime Time, double Value)> history, double value)
    {
        var now = DateTime.UtcNow;
        history.Enqueue((now, value));
        while (history.Count > 0 && (now - history.Peek().Time).TotalSeconds > MaxHistorySeconds + 5) history.Dequeue(); // +5: the 5m view still needs its off-screen sample
    }

    /// <summary>
    /// Redraws every graph once per composed frame (vsync-paced, typically 60fps) instead of once per
    /// data sample. The x-axis is real elapsed time, not sample index, so the whole curve keeps scrolling
    /// smoothly between the once-a-second samples instead of jumping. Throttled to ~30fps in case the
    /// display composes faster than that.
    /// </summary>
    private bool _renderingHooked;

    private void UpdateRenderingHook()
    {
        bool wanted = IsVisible && WindowState != WindowState.Minimized
                      && Tabs.SelectedItem is System.Windows.Controls.TabItem { Header: "Overview" or "Hardware" };
        if (wanted == _renderingHooked) return;
        _renderingHooked = wanted;
        if (wanted) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRender).TotalMilliseconds < 33) return; // ~30fps: plenty fluid, a fraction of the cost of 60
        _lastRender = now;
        foreach (var g in _liveGraphs)
        {
            if (!g.Container.IsVisible) continue; // WPF only keeps the active tab's content in the visual tree
            DrawSparkline(g, now);
        }
    }

    /// <summary>
    /// Smooth, non-overshooting curve — vezi vault Shelf/Grafice si dashboard: each segment is a cubic
    /// Bezier whose control points sit at the segment's horizontal midpoint but keep their own endpoint's
    /// height, so the curve stays flat at every sample and can never travel outside the two points it joins
    /// (unlike a Catmull-Rom spline, which dips past low points). The area fill reuses the same curve,
    /// closed down to the baseline, like the "Played per day" chart it's modelled on.
    /// </summary>
    private void DrawSparkline(GraphBinding g, DateTime now)
    {
        var (container, line, fill, history, autoScale) = (g.Container, g.Line, g.Fill, g.History, g.AutoScale);
        // "Per thread" hides the main CPU sparkline/fill in favor of the per-core mini graphs, but leaves
        // CpuGraphGrid itself visible (it also hosts the thread grid) — without this, the hidden card's own
        // axis gridlines kept repositioning off the height of the ever-growing thread grid beneath them.
        if (line.Visibility != Visibility.Visible)
        {
            if (g.GridLines != null)
                foreach (var el in g.GridLines.Cast<UIElement>().Concat(g.GridLabels!)) el.Visibility = Visibility.Collapsed;
            if (g.Dot != null) g.Dot.Visibility = Visibility.Collapsed;
            return;
        }
        double width = container.ActualWidth > 0 ? container.ActualWidth : 220;
        double height = container.ActualHeight > 0 ? container.ActualHeight : 122;

        var windowStart = now.AddSeconds(-_historyWindowSeconds);
        // Keep one sample past the left edge so the oldest segment slides out under the card's clip
        // instead of vanishing the moment its start point crosses x=0.
        var points = history.ToList();
        int first = points.FindIndex(s => s.Time >= windowStart);
        if (first < 0) { line.Data = null; fill.Data = null; if (g.Dot != null) g.Dot.Visibility = Visibility.Collapsed; return; }
        points.RemoveRange(0, Math.Max(0, first - 1));

        // Ease the newest value in instead of snapping to it the instant a sample lands — a fresh sample
        // is otherwise a hard vertex appearing mid-frame, which reads as a jump right where the eye is.
        double latestReal = points[^1].Value;
        g.DisplayValue = double.IsNaN(g.DisplayValue) ? latestReal : g.DisplayValue + (latestReal - g.DisplayValue) * 0.2;
        points[^1] = (points[^1].Time, g.DisplayValue);

        // Extend the eased value up to "now" every frame, so the right edge stays live and only the
        // left edge (data aging out of the window) visibly scrolls — the actual source of the motion.
        if (points[^1].Time < now) points.Add((now, g.DisplayValue));
        if (points.Count < 2) { line.Data = null; fill.Data = null; return; }

        // CPU/Memory/GPU are already 0-100%; disk/network metrics (MB/s, Mbps, ms) have no fixed
        // ceiling, so scale those to the window's own peak instead of clamping to a percentage.
        double max = autoScale ? Math.Max(1, points.Max(p => p.Value)) : 100;
        if (g.ShowAxis) UpdateAxisGridlines(g, width, height, max, autoScale, g.AxisTopInset);
        // The live end stops short of the right edge, so its dot shows whole instead of cut in half.
        double right = g.ShowAxis ? width - LiveDotSize : width;
        double XOf(DateTime t) => right * (1 - (now - t).TotalSeconds / _historyWindowSeconds);
        double YOf(double v) => height - Math.Clamp(v, 0, max) / max * height;

        var figure = new PathFigure { StartPoint = new Point(XOf(points[0].Time), YOf(points[0].Value)) };
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = new Point(XOf(points[i].Time), YOf(points[i].Value));
            var p1 = new Point(XOf(points[i + 1].Time), YOf(points[i + 1].Value));
            double midX = (p0.X + p1.X) / 2;
            figure.Segments.Add(new BezierSegment(new Point(midX, p0.Y), new Point(midX, p1.Y), p1, true));
        }

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(figure);
        line.Data = lineGeometry;

        // Close straight down from the last point and back up under the first — never to the card's own
        // corners. When there isn't yet enough history to fill the window (just after launch, or a few
        // minutes in on the 5-minute view), the real data doesn't reach x=0, and closing to the literal
        // corner instead of under the first point drew a diagonal cutting clean across the card.
        var areaFigure = figure.Clone();
        areaFigure.Segments.Add(new LineSegment(new Point(right, height), true)); // last point's X is always "now"
        areaFigure.Segments.Add(new LineSegment(new Point(figure.StartPoint.X, height), true));
        areaFigure.IsClosed = true;
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        fill.Data = areaGeometry;

        // Where the recording begins mid-card (just after launch, or on a longer window) the curve and its
        // fill rise out of nothing over a few dozen pixels instead of starting at a hard vertical wall. Once
        // the data reaches the left edge there's no wall, and no fade: on a tall fill it would read as a band.
        double fadeStart = figure.StartPoint.X;
        if (g.FadeIn == null)
        {
            g.FadeIn = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
            g.FadeIn.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            g.FadeIn.GradientStops.Add(new GradientStop(Colors.Black, 1));
        }
        line.OpacityMask = fill.OpacityMask = fadeStart > 1 ? g.FadeIn : null;
        g.FadeIn.StartPoint = new Point(fadeStart, 0);
        g.FadeIn.EndPoint = new Point(fadeStart + 36, 0);

        if (!g.ShowAxis) return; // the per-thread minis stay plain
        if (g.Dot == null)
        {
            g.Dot = new System.Windows.Shapes.Ellipse { Width = LiveDotSize, Height = LiveDotSize, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            g.Dot.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(line.Stroke)) { Source = line });
            g.Dot.SetBinding(UIElement.EffectProperty, new System.Windows.Data.Binding(nameof(line.Effect)) { Source = line });
            ((System.Windows.Controls.Panel)container).Children.Add(g.Dot);
        }
        g.Dot.Visibility = Visibility.Visible;
        g.Dot.Margin = new Thickness(right - LiveDotSize / 2, YOf(g.DisplayValue) - LiveDotSize / 2, 0, 0);
    }

    private const double LiveDotSize = 7;

    /// <summary>Three subtle horizontal lines with their value to the right, like Task Manager's graph scale — built once per card, repositioned every frame since autoscaled cards (disk/network) change their max as new peaks come in.</summary>
    private static void UpdateAxisGridlines(GraphBinding g, double width, double height, double max, bool autoScale, double topInset)
    {
        if (g.GridLines == null)
        {
            var panel = (System.Windows.Controls.Panel)g.Container;
            g.GridLines = new System.Windows.Shapes.Line[AxisFractions.Length];
            g.GridLabels = new System.Windows.Controls.TextBlock[AxisFractions.Length];
            for (int i = 0; i < AxisFractions.Length; i++)
            {
                var gridLine = new System.Windows.Shapes.Line { StrokeThickness = 1, Opacity = 0.18, StrokeDashArray = new DoubleCollection { 2, 4 } };
                gridLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
                var label = new System.Windows.Controls.TextBlock { FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
                panel.Children.Insert(0, gridLine); // behind the fill/line, drawn first
                panel.Children.Insert(1, label);
                g.GridLines[i] = gridLine;
                g.GridLabels[i] = label;
            }
        }

        for (int i = 0; i < AxisFractions.Length; i++)
        {
            double y = AxisFractions[i] * height;
            g.GridLabels![i].Visibility = Visibility.Visible;
            // Only the middle line: the card's own top and bottom edges are 100% and 0%.
            g.GridLines[i].Visibility = AxisFractions[i] is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            g.GridLines[i].X1 = 0; g.GridLines[i].X2 = width; g.GridLines[i].Y1 = y; g.GridLines[i].Y2 = y;
            double value = max * (1 - AxisFractions[i]);
            g.GridLabels![i].Text = autoScale ? $"{value:0.#}" : $"{value:0}%"; // an idle 1 MB/s scale read "1, 1, 0"
            g.GridLabels[i].Margin = new Thickness(0, Math.Clamp(y - 7, topInset, height - 14), 8, 0);
        }
    }

    // --- Curățare ---

    private void AnalyzeCleanup_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _cleanupItems)
        {
            string? path = ((Func<string?>)item.Tag!)();
            item.Note = CleanupService.FormatSize(CleanupService.GetSize(path));
        }
        CleanupList.Items.Refresh();
        Log("Analysis complete.");
    }

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        long totalFreed = 0;
        foreach (var item in _cleanupItems.Where(i => i.IsChecked))
        {
            string? path = ((Func<string?>)item.Tag!)();
            var (deleted, skipped, freed) = CleanupService.Clean(path);
            totalFreed += freed;
            Log($"{item.Name}: {deleted} files deleted, {skipped} skipped (in use), {CleanupService.FormatSize(freed)} freed.");
            item.Note = CleanupService.FormatSize(CleanupService.GetSize(path));
        }
        CleanupList.Items.Refresh();
        Log($"Total freed: {CleanupService.FormatSize(totalFreed)}.");
    }

    // --- Memorie ---

    private void TrimMemory_Click(object sender, RoutedEventArgs e)
    {
        var (trimmed, freed) = MemoryService.TrimAll();
        Log($"Memory freed from {trimmed} processes: {CleanupService.FormatSize(freed)}.");
    }

    // --- Servicii ---

    /// <summary>The managed services' states, on this PC or the one being shown.</summary>
    private async Task LoadServices()
    {
        var source = _source;
        Dictionary<string, string> statuses;
        try
        {
            statuses = await Task.Run(() => source == null
                ? ServiceManager.KnownServices.ToDictionary(s => s.Key, s => ServiceManager.GetStatus(s.Key))
                : JsonSerializer.Deserialize<Dictionary<string, string>>(source.List("services"), LiveJson) ?? new());
        }
        catch (Exception ex) when (ex is RemoteHistoryException or JsonException)
        {
            if (source == _source) Log($"Services on {source!.Name}: {ex.Message}.");
            statuses = new();
        }
        if (source != _source) return;
        foreach (var item in _serviceItems) item.Note = statuses.GetValueOrDefault((string)item.Tag!, "—");
        ServicesList.Items.Refresh();
    }

    private async void DisableServices_Click(object sender, RoutedEventArgs e)
    {
        if (_source is { } target)
        {
            foreach (var item in _serviceItems.Where(i => i.IsChecked).ToList())
                await RunOn(target, $"Stopping and disabling {item.Name}", t => t.Run("service-disable", (string)item.Tag!));
            await LoadServices();
            return;
        }
        foreach (var item in _serviceItems.Where(i => i.IsChecked))
        {
            ServiceManager.StopAndDisable((string)item.Tag!);
            item.Note = ServiceManager.GetStatus((string)item.Tag!);
            Log($"{item.Name}: stopped and disabled.");
        }
        ServicesList.Items.Refresh();
    }

    private async void EnableServices_Click(object sender, RoutedEventArgs e)
    {
        if (_source is { } target)
        {
            foreach (var item in _serviceItems.Where(i => i.IsChecked).ToList())
                await RunOn(target, $"Re-enabling {item.Name}", t => t.Run("service-enable", (string)item.Tag!));
            await LoadServices();
            return;
        }
        foreach (var item in _serviceItems.Where(i => i.IsChecked))
        {
            ServiceManager.EnableAndStart((string)item.Tag!);
            item.Note = ServiceManager.GetStatus((string)item.Tag!);
            Log($"{item.Name}: re-enabled.");
        }
        ServicesList.Items.Refresh();
    }

    // --- Tweaks ---

    private void ApplyTweaks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _tweakItems.Where(i => i.IsChecked))
        {
            var tweak = (Tweak)item.Tag!;
            try { tweak.Apply(); item.Note = "Applied"; Log($"{tweak.Name}: applied."); }
            catch (Exception ex) { Log($"{tweak.Name}: error — {ex.Message}"); }
        }
        TweaksList.Items.Refresh();
    }

    private void RevertTweaks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _tweakItems.Where(i => i.IsChecked))
        {
            var tweak = (Tweak)item.Tag!;
            try { tweak.Revert(); item.Note = ""; Log($"{tweak.Name}: reverted."); }
            catch (Exception ex) { Log($"{tweak.Name}: error — {ex.Message}"); }
        }
        TweaksList.Items.Refresh();
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        Log("Creating restore point...");
        var (ok, message) = await Task.Run(TweakService.CreateRestorePoint);
        Log(ok ? message : $"Restore point failed — {message}");
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        TweakService.RestartExplorer();
        Log("Explorer restarted.");
    }

    private void ExportTweaks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "sysoptimizer-tweaks.json" };
        if (dialog.ShowDialog() != true) return;
        TweakService.ExportProfile(dialog.FileName, _tweaks);
        Log($"Tweaks exported to {dialog.FileName}.");
    }

    private void ImportTweaks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        foreach (var line in TweakService.ImportProfile(dialog.FileName, _tweaks)) Log(line);
        foreach (var item in _tweakItems) item.Note = ((Tweak)item.Tag!).IsApplied() ? "Applied" : "";
        TweaksList.Items.Refresh();
    }

    // --- Startup ---

    /// <summary>The startup entries and the last Windows starts, on this PC or the one being shown.</summary>
    private async Task LoadStartup()
    {
        var source = _source;
        List<StartupService.StartupItem> items;
        List<Boot> boots;
        try
        {
            (items, boots) = await Task.Run(() =>
            {
                if (source == null) return (StartupService.GetItems(), BootService.Read());
                var startup = JsonSerializer.Deserialize<List<StartupService.StartupItem>>(source.List("startup"), LiveJson) ?? new();
                try { return (startup, JsonSerializer.Deserialize<List<Boot>>(source.List("boots"), LiveJson) ?? new()); }
                catch (Exception ex) when (ex is RemoteHistoryException or JsonException) { return (startup, new List<Boot>()); }
            });
        }
        catch (Exception ex) when (ex is RemoteHistoryException or JsonException)
        {
            if (source != _source) return;
            _startupItems.Clear();
            BootsList.ItemsSource = null;
            BootsSummary.Text = $"{source!.Name}: {ex.Message}.";
            return;
        }
        if (source != _source) return;
        _startupItems.Clear();
        foreach (var item in items)
            _startupItems.Add(new OptionItem { Name = item.Name, Description = item.Command, Note = item.Location == "Disabled" ? "Disabled" : "Enabled", Tag = item });
        ShowBoots(boots, source?.Name ?? "this PC");
    }

    private void ShowBoots(List<Boot> boots, string pc)
    {
        BootsList.ItemsSource = boots.Take(10).Select(b => new OptionItem
        {
            Name = $"{b.StartUtc.ToLocalTime():ddd d MMM, HH:mm} — {b.Seconds:0} s",
            Description = b.SlowedBy.Count > 0
                ? "Held up by " + string.Join(", ", b.SlowedBy.Take(3).Select(s => $"{s.Name} (+{s.ExtraSeconds:0.#} s)"))
                : $"Desktop after {b.DesktopSeconds:0} s · {b.StartupApps} apps starting with it",
        }).ToList();
        if (boots.Count == 0) { BootsSummary.Text = $"Windows hasn't recorded a start on {pc} (its boot diagnostics may be off)."; return; }
        // The newest five against the ones before: a startup app added since shows as a jump.
        var recent = boots.Take(5).ToList();
        var earlier = boots.Skip(5).ToList();
        double trend = earlier.Count >= 3 ? recent.Average(b => b.Seconds) - earlier.Average(b => b.Seconds) : 0;
        BootsSummary.Text = $"Windows starts on {pc}: {recent.Average(b => b.Seconds):0} s on average lately"
            + (Math.Abs(trend) >= 3 ? $" — {Math.Abs(trend):0} s {(trend > 0 ? "slower" : "faster")} than the {earlier.Count} before" : "")
            + ". From power-on to a settled desktop, as Windows measures it.";
    }

    private async void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_source is { } target)
        {
            foreach (var item in _startupItems.Where(i => i.IsChecked).ToList())
                await RunOn(target, $"Enabling {item.Name} at startup", t => t.Run("startup-enable", item.Name));
            await LoadStartup();
            return;
        }
        foreach (var item in _startupItems.Where(i => i.IsChecked))
        {
            var startupItem = (StartupService.StartupItem)item.Tag!;
            StartupService.Enable(startupItem);
            item.Note = "Enabled";
            Log($"{startupItem.Name}: enabled at startup.");
        }
        StartupList.Items.Refresh();
    }

    private async void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_source is { } target)
        {
            foreach (var item in _startupItems.Where(i => i.IsChecked).ToList())
                await RunOn(target, $"Disabling {item.Name} at startup", t => t.Run("startup-disable", item.Name));
            await LoadStartup();
            return;
        }
        foreach (var item in _startupItems.Where(i => i.IsChecked))
        {
            var startupItem = (StartupService.StartupItem)item.Tag!;
            StartupService.Disable(startupItem);
            item.Note = "Disabled";
            Log($"{startupItem.Name}: disabled at startup.");
        }
        StartupList.Items.Refresh();
    }

    // --- Network / DNS ---

    private void SetDns_Click(object sender, RoutedEventArgs e)
    {
        if (DnsAdapterCombo.SelectedItem is not string adapter) { Log("Pick a network adapter first."); return; }
        int presetIndex = int.Parse((string)((System.Windows.Controls.Button)sender).Tag);
        var (name, primary, secondary) = DnsService.Presets[presetIndex];
        bool ok = DnsService.SetDns(adapter, primary, secondary);
        Log(ok ? $"{adapter}: DNS set to {name}." : $"{adapter}: failed to set DNS to {name}.");
    }

    // --- Debloat ---

    private async void RemoveBloatware_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _bloatwareItems.Where(i => i.IsChecked).ToList())
        {
            string package = (string)item.Tag!;
            Log($"{item.Name}: removing...");
            var (ok, message) = await Task.Run(() =>
            {
                bool result = BloatwareService.Remove(package, out string msg);
                return (result, msg);
            });
            item.Note = ok ? "Not installed" : "";
            Log($"{item.Name}: {(ok ? "removed." : "failed — " + message)}");
        }
        BloatwareList.Items.Refresh();
    }

    // --- Aplicații ---

    private async void InstallApps_Click(object sender, RoutedEventArgs e)
    {
        var target = _source; // the PC being shown when the button was clicked
        foreach (var item in _appItems.Where(i => i.IsChecked).ToList())
        {
            string id = (string)item.Tag!;
            if (target != null) { await RunOn(target, $"Installing {item.Name}", t => t.Run("install-winget", id)); continue; }
            Log($"{item.Name}: installing...");
            var (ok, output) = await Task.Run(() =>
            {
                bool result = WingetService.Install(id, out string outp);
                return (result, outp);
            });
            Log($"{item.Name}: {(ok ? "installed." : "failed — " + FirstLine(output))}");
        }
    }

    private void InstallSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SearchApps_Click(sender, e);
    }

    /// <summary>The whole winget catalog: the matches replace the popular list until the search is cleared.</summary>
    private async void SearchApps_Click(object sender, RoutedEventArgs e)
    {
        string query = InstallSearchBox.Text.Trim();
        if (query.Length == 0) { ShowPopularApps(); return; }
        int search = ++_appSearch;
        InstallSearchButton.IsEnabled = false;
        InstallSearchStatus.Text = $"Searching for “{query}”…";
        try
        {
            var found = await Task.Run(() => WingetService.Search(query));
            if (search != _appSearch) return; // cleared, or searched again, meanwhile
            _appItems.Clear();
            foreach (var r in found.Where(r => RemoteCommand.IsWingetId(r.Id)))
                _appItems.Add(new OptionItem { Name = r.Name, Description = $"{r.Id} · {r.Version}", Tag = r.Id });
            InstallSearchStatus.Text = found.Count == 0 ? $"Nothing found for “{query}”." : $"{found.Count} found for “{query}”.";
            InstallClearButton.Visibility = Visibility.Visible;
        }
        catch (System.ComponentModel.Win32Exception) { InstallSearchStatus.Text = "winget isn't installed — get “App Installer” from the Microsoft Store."; }
        finally { InstallSearchButton.IsEnabled = true; }
    }

    private int _appSearch; // the latest search; an older one finishing late is dropped

    private void ShowPopularApps()
    {
        _appSearch++;
        _appItems.Clear();
        foreach (var a in WingetService.PopularApps) _appItems.Add(new OptionItem { Name = a.Name, Tag = a.Id });
        InstallSearchStatus.Text = "Popular apps";
        InstallClearButton.Visibility = Visibility.Collapsed;
    }

    private void ClearAppSearch_Click(object sender, RoutedEventArgs e)
    {
        InstallSearchBox.Clear(); // its TextChanged brings the popular list back
        InstallSearchBox.Focus();
    }

    private void InstallSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded && InstallSearchBox.Text.Trim().Length == 0 && InstallSearchStatus.Text != "Popular apps") ShowPopularApps(); // also drops a search still running
    }

    private async void UpgradeAll_Click(object sender, RoutedEventArgs e)
    {
        Log("Updating installed apps via winget...");
        var (ok, output) = await Task.Run(() =>
        {
            bool result = WingetService.UpgradeAll(out string outp);
            return (result, outp);
        });
        Log(ok ? "Update complete." : "Update failed — " + FirstLine(output));
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";

    // --- Health ---

    private async Task RefreshHealth()
    {
        var report = await Task.Run(HealthService.Check);
        HealthScoreText.Text = $"{report.Score}";
        // Theme keys, not fixed colors: each palette has its own good / warn / bad, and a theme switch repaints them.
        string key = report.Score >= 75 ? "StatusGood" : report.Score >= 35 ? "StatusWarn" : "StatusBad";
        HealthScoreText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, key);
        HealthCardBorder.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, key);
        HealthIssuesList.ItemsSource = report.Issues;
    }

    private async void OptimizeNow_Click(object sender, RoutedEventArgs e)
    {
        OptimizeNowButton.IsEnabled = false;
        Log("Optimizing...");
        var lines = await Task.Run(HealthService.Optimize);
        foreach (var line in lines) Log(line);

        foreach (var item in _cleanupItems) item.Note = CleanupService.FormatSize(CleanupService.GetSize(((Func<string?>)item.Tag!)()));
        foreach (var item in _serviceItems) item.Note = ServiceManager.GetStatus((string)item.Tag!);
        CleanupList.Items.Refresh();
        ServicesList.Items.Refresh();
        await RefreshHealth();
        OptimizeNowButton.IsEnabled = true;
        Log("Optimize complete.");
    }

    // --- Duplicate files ---

    private void BrowseDuplicatesFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) DuplicatesFolderBox.Text = dialog.SelectedPath;
    }

    private async void ScanDuplicates_Click(object sender, RoutedEventArgs e)
    {
        string folder = DuplicatesFolderBox.Text.Trim();
        if (!Directory.Exists(folder)) { Log("Pick a folder that exists first."); return; }

        Log($"Scanning {folder} for duplicates...");
        var groups = await Task.Run(() => FileScanService.FindDuplicates(folder));
        _duplicateGroups.Clear();
        foreach (var group in groups)
        {
            var files = new ObservableCollection<OptionItem>();
            for (int i = 0; i < group.Paths.Count; i++)
                files.Add(new OptionItem { Description = group.Paths[i], IsChecked = i > 0, Tag = group.Paths[i] }); // first copy kept by default
            _duplicateGroups.Add(new DuplicateGroupView { Header = $"{CleanupService.FormatSize(group.Size)} × {group.Paths.Count} copies", Files = files });
        }
        Log($"Found {groups.Count} duplicate group{(groups.Count == 1 ? "" : "s")}.");
    }

    private void DeleteDuplicates_Click(object sender, RoutedEventArgs e)
    {
        long freed = 0;
        int deleted = 0;
        foreach (var group in _duplicateGroups.ToList())
        {
            foreach (var file in group.Files.Where(f => f.IsChecked).ToList())
            {
                try { freed += new FileInfo((string)file.Tag!).Length; File.Delete((string)file.Tag!); deleted++; group.Files.Remove(file); }
                catch (Exception ex) { Log($"{file.Description}: failed — {ex.Message}"); }
            }
            if (group.Files.Count < 2) _duplicateGroups.Remove(group);
        }
        Log($"Deleted {deleted} files, {CleanupService.FormatSize(freed)} freed.");
    }

    // --- Large files ---

    private void BrowseLargeFilesFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) LargeFilesFolderBox.Text = dialog.SelectedPath;
    }

    private async void ScanLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        string folder = LargeFilesFolderBox.Text.Trim();
        if (!Directory.Exists(folder)) { Log("Pick a folder that exists first."); return; }

        Log($"Scanning {folder} for large files...");
        var files = await Task.Run(() => FileScanService.FindLargest(folder));
        _largeFileItems.Clear();
        foreach (var file in files)
            _largeFileItems.Add(new OptionItem { Name = Path.GetFileName(file.Path), Description = file.Path, Note = CleanupService.FormatSize(file.Size), Tag = file.Path });
        Log($"Found {files.Count} files.");
    }

    private void DeleteLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        long freed = 0;
        int deleted = 0;
        foreach (var item in _largeFileItems.Where(i => i.IsChecked).ToList())
        {
            try { freed += new FileInfo((string)item.Tag!).Length; File.Delete((string)item.Tag!); deleted++; _largeFileItems.Remove(item); }
            catch (Exception ex) { Log($"{item.Name}: failed — {ex.Message}"); }
        }
        Log($"Deleted {deleted} files, {CleanupService.FormatSize(freed)} freed.");
    }

    // --- Uninstall ---

    private async void UninstallApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _installedAppItems.Where(i => i.IsChecked).ToList())
        {
            var app = (InstalledApp)item.Tag!;
            Log($"{app.Name}: launching uninstaller...");
            var (ok, message) = await Task.Run(() =>
            {
                bool result = UninstallService.Uninstall(app, out string msg);
                return (result, msg);
            });
            if (!ok) { Log($"{app.Name}: failed — {message}"); continue; }

            _installedAppItems.Remove(item);
            string? leftover = UninstallService.FindLeftoverFolder(app);
            if (leftover != null)
            {
                _leftoverItems.Add(new OptionItem { Name = app.Name, Description = leftover, Tag = leftover });
                DeleteLeftoversButton.Visibility = Visibility.Visible;
                Log($"{app.Name}: uninstalled — its install folder is still there.");
            }
            else
            {
                Log($"{app.Name}: uninstalled.");
            }
        }
    }

    private void DeleteLeftovers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _leftoverItems.Where(i => i.IsChecked).ToList())
        {
            try { Directory.Delete((string)item.Tag!, recursive: true); Log($"{item.Name}: leftover folder removed."); _leftoverItems.Remove(item); }
            catch (Exception ex) { Log($"{item.Name}: failed — {ex.Message}"); }
        }
        if (_leftoverItems.Count == 0) DeleteLeftoversButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Border.ClipToBounds clips children to the rectangular layout box, not the rounded silhouette its
    /// CornerRadius actually paints — a full-bleed graph behind the card's text otherwise pokes square
    /// corners out past the rounded card underneath it.
    /// </summary>
    private void ApplyRoundedClip(System.Windows.Controls.Border border)
    {
        void Update()
        {
            if (border.ActualWidth <= 0 || border.ActualHeight <= 0) return;
            double radius = border.CornerRadius.TopLeft;
            border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
        }
        border.SizeChanged += (_, _) => Update();
        _clipUpdaters.Add(Update);
        Update();
    }

    private readonly List<Action> _clipUpdaters = new();

    /// <summary>The pills at the top of a grouped tab: shows the page the pill names, hides its siblings.</summary>
    private void SubPage_Checked(object sender, RoutedEventArgs e)
    {
        // Tag binds by ElementName, which isn't resolved yet when the first pill is checked from XAML —
        // that page is the visible one already.
        if (sender is not System.Windows.Controls.RadioButton { Tag: FrameworkElement page } || page.Parent is not System.Windows.Controls.Panel pages) return;
        foreach (UIElement child in pages.Children) child.Visibility = child == page ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _savedTheme = ThemeManager.Current.Key;

    /// <summary>
    /// The theme list: each entry with a small plate in that theme's own colours (its surface, the three graph
    /// colours and its accent) and its name in its own font. Hovering an entry puts the whole window in that
    /// theme; closing the list keeps the one clicked, or goes back.
    /// </summary>
    private void InitThemePicker()
    {
        foreach (var theme in ThemeManager.Themes)
        {
            var colours = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{theme.Key}.xaml", UriKind.Absolute) };
            var dots = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var key in new[] { "AccentCpu", "AccentMem", "AccentGpu", "Accent" })
                dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(2, 0, 2, 0), Fill = (Brush)colours[key] });
            var plate = new System.Windows.Controls.Border
            {
                Width = 52, Height = 18, Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Background = (Brush)colours["Surface"], BorderBrush = (Brush)colours["Border"], Child = dots,
            };
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(plate);
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = theme.Label, FontFamily = (FontFamily)colours["UiFont"], VerticalAlignment = VerticalAlignment.Center });
            var item = new System.Windows.Controls.ComboBoxItem { Content = row, Tag = theme.Key };
            System.Windows.Automation.AutomationProperties.SetName(item, theme.Label);
            item.MouseEnter += (_, _) => { if (ThemeCombo.IsDropDownOpen && ThemeManager.Current.Key != theme.Key) ThemeManager.Apply(theme.Key, save: false); };
            ThemeCombo.Items.Add(item);
        }
        ThemeCombo.SelectedItem = ThemeCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>().First(i => (string)i.Tag == ThemeManager.Current.Key);
        ThemeCombo.DropDownClosed += (_, _) => KeepTheme();
        ThemeCombo.SelectionChanged += (_, _) => { if (!ThemeCombo.IsDropDownOpen) KeepTheme(); }; // the keyboard, with the list shut
    }

    /// <summary>The theme picked in the list stays (and is saved); a hovered one that wasn't picked is undone.</summary>
    private void KeepTheme()
    {
        if (ThemeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string key }) return;
        if (key != _savedTheme) { ThemeManager.Apply(key); _savedTheme = key; }
        else if (ThemeManager.Current.Key != key) ThemeManager.Apply(key, save: false);
    }

    private void OnThemeChanged()
    {
        WindowGlass.ApplyChrome(this);
        if (_updateDialog != null) WindowGlass.ApplyChrome(_updateDialog); // modeless: it can be open across a switch
        foreach (Window owned in OwnedWindows) WindowGlass.ApplyChrome(owned); // Transfers, Files on…, a PC's log
        // The corner radius is a resource reference; wait for it to settle before re-cutting the clips.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _clipUpdaters.ForEach(update => update()));
    }
}
