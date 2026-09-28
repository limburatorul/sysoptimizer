using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
        public double DisplayValue = double.NaN; // eased towards the latest real sample — see DrawSparkline
    }
    private readonly List<GraphBinding> _liveGraphs = new();
    private DateTime _lastRender = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowGlass.EnableAcrylic(this);
        Closed += (_, _) => { _resourceTimer?.Stop(); _resourceMonitor?.Dispose(); CompositionTarget.Rendering -= OnRendering; ThemeManager.ThemeChanged -= OnThemeChanged; _updateTimer.Stop(); };
        ThemeManager.ThemeChanged += OnThemeChanged;
        (ThemeManager.Current.Key switch { "StarTrek" => ThemeStarTrek, "StarCraft" => ThemeStarCraft, _ => ThemeGlass }).IsChecked = true;

        _liveGraphs.Add(new GraphBinding { Container = CpuGraphGrid, Line = CpuSparkline, Fill = CpuAreaFill, History = _cpuHistory, AutoScale = false });
        _liveGraphs.Add(new GraphBinding { Container = MemGraphGrid, Line = MemSparkline, Fill = MemAreaFill, History = _memHistory, AutoScale = false });
        _liveGraphs.Add(new GraphBinding { Container = GpuGraphGrid, Line = GpuSparkline, Fill = GpuAreaFill, History = _gpuHistory, AutoScale = false });
        CompositionTarget.Rendering += OnRendering;

        ApplyRoundedClip(CpuCardBorder);
        ApplyRoundedClip(MemCardBorder);
        ApplyRoundedClip(GpuCardBorder);
        CpuThreadsGrid.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutCpuThreads(); };

        StartResourceMonitor();

        CleanupList.ItemsSource = _cleanupItems;
        ServicesList.ItemsSource = _serviceItems;
        TweaksList.ItemsSource = _tweakItems;
        AppsList.ItemsSource = _appItems;
        StartupList.ItemsSource = _startupItems;
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

    private void Log(string message) => LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";

    // --- Resources ---

    private void StartResourceMonitor()
    {
        try { _resourceMonitor = new ResourceMonitor(); }
        catch (Exception ex) { Log($"Resource monitor unavailable — {ex.Message}"); return; }

        _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _resourceTimer.Tick += async (_, _) => await UpdateResources();
        _resourceTimer.Start();
        _ = UpdateResources();
    }

    private async Task UpdateResources()
    {
        // Sample() pings each adapter's gateway, which can take up to ~300ms per NIC — keep that off the UI thread.
        var snap = await Task.Run(() => _resourceMonitor!.Sample());

        CpuPercentText.Text = $"{snap.CpuPercent:0}%";
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
            GpuDetailText.Text = $"{snap.GpuMemUsedGB:0.#} GB VRAM";
            PushHistory(_gpuHistory, snap.GpuPercent);
        }

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
        CpuThreadsGrid.Children.Add(cell);
        LayoutCpuThreads();
        _liveGraphs.Add(new GraphBinding { Container = graph, Line = line, Fill = fill, History = history, AutoScale = false });
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
        double width = container.ActualWidth > 0 ? container.ActualWidth : 220;
        double height = container.ActualHeight > 0 ? container.ActualHeight : 122;

        var windowStart = now.AddSeconds(-_historyWindowSeconds);
        // Keep one sample past the left edge so the oldest segment slides out under the card's clip
        // instead of vanishing the moment its start point crosses x=0.
        var points = history.ToList();
        int first = points.FindIndex(s => s.Time >= windowStart);
        if (first < 0) { line.Data = null; fill.Data = null; return; }
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
        double XOf(DateTime t) => width * (1 - (now - t).TotalSeconds / _historyWindowSeconds);
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
        areaFigure.Segments.Add(new LineSegment(new Point(width, height), true)); // last point's X is always "now" == width
        areaFigure.Segments.Add(new LineSegment(new Point(figure.StartPoint.X, height), true));
        areaFigure.IsClosed = true;
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        fill.Data = areaGeometry;
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

    private void DisableServices_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _serviceItems.Where(i => i.IsChecked))
        {
            ServiceManager.StopAndDisable((string)item.Tag!);
            item.Note = ServiceManager.GetStatus((string)item.Tag!);
            Log($"{item.Name}: stopped and disabled.");
        }
        ServicesList.Items.Refresh();
    }

    private void EnableServices_Click(object sender, RoutedEventArgs e)
    {
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

    private void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _startupItems.Where(i => i.IsChecked))
        {
            var startupItem = (StartupService.StartupItem)item.Tag!;
            StartupService.Enable(startupItem);
            item.Note = "Enabled";
            Log($"{startupItem.Name}: enabled at startup.");
        }
        StartupList.Items.Refresh();
    }

    private void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
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
        foreach (var item in _appItems.Where(i => i.IsChecked).ToList())
        {
            string id = (string)item.Tag!;
            Log($"{item.Name}: installing...");
            var (ok, output) = await Task.Run(() =>
            {
                bool result = WingetService.Install(id, out string outp);
                return (result, outp);
            });
            Log($"{item.Name}: {(ok ? "installed." : "failed — " + FirstLine(output))}");
        }
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
        var color = report.Score >= 75 ? Color.FromRgb(0x22, 0xC5, 0x5E)
                  : report.Score >= 35 ? Color.FromRgb(0xF9, 0x73, 0x16)
                  : Color.FromRgb(0xEF, 0x44, 0x44);
        HealthScoreText.Foreground = new SolidColorBrush(color);
        HealthCardBorder.BorderBrush = new SolidColorBrush(color);
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

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string key } && key != ThemeManager.Current.Key)
            ThemeManager.Apply(key);
    }

    private void OnThemeChanged()
    {
        WindowGlass.ApplyChrome(this);
        // The corner radius is a resource reference; wait for it to settle before re-cutting the clips.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _clipUpdaters.ForEach(update => update()));
    }
}
