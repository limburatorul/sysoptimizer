using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;
using Path = System.Windows.Shapes.Path;

namespace Sysoptimizer;

/// <summary>
/// Activity → App history: one app at a time over the last hour, day, week or month — its CPU, memory and GPU,
/// its sessions, its share of what the PC used — from the app index (HistoryStore.ReadApps). Where the app
/// isn't in a snapshot it is drawn as "not recorded", never as 0.
/// </summary>
public partial class MainWindow
{
    private AppHistory _apps = new(HistoryStore.AppSlotSeconds, new(), new());
    private List<AppTotal> _appTotals = new();
    private DateTime _appsFrom, _appsTo;
    private string? _appSelected;
    private int _appsTicket;

    private void InitAppHistory()
    {
        AppHistoryList.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _ = LoadAppHistory(); };
        foreach (var chart in new[] { AppCpuChart, AppMemChart, AppGpuChart }) chart.SizeChanged += (_, _) => DrawAppChart(chart);
        ThemeManager.ThemeChanged += () => { if (AppHistoryList.IsVisible) ShowApp(); };
    }

    private void AppRange_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) _ = LoadAppHistory(); }
    private void AppSearch_Changed(object sender, TextChangedEventArgs e) => FillAppList();
    private void AppSort_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) FillAppList(); }

    private async Task LoadAppHistory()
    {
        int ticket = ++_appsTicket;
        double hours = AppRangeCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? double.Parse(tag, CultureInfo.InvariantCulture) : 24;
        var to = DateTime.Now;
        var from = to.AddHours(-hours);
        AppListTitle.Text = "LOADING…";
        var (apps, totals) = await Task.Run(() =>
        {
            var read = HistoryStore.ReadApps(from, to);
            return (read, HistoryStore.AppTotals(read));
        });
        if (ticket != _appsTicket) return; // the range changed meanwhile
        (_apps, _appTotals, _appsFrom, _appsTo) = (apps, totals, from, to);
        FillAppList();
        ShowApp();
    }

    private void FillAppList()
    {
        if (!IsLoaded) return;
        string sort = AppSortCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "cpu";
        string search = AppSearchBox.Text.Trim();
        var shown = _appTotals.Where(t => search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        shown = sort switch
        {
            "peakcpu" => shown.OrderByDescending(t => t.PeakCpu),
            "mem" => shown.OrderByDescending(t => t.RamMB),
            "peakmem" => shown.OrderByDescending(t => t.PeakRamMB),
            "gpu" => shown.OrderByDescending(t => t.Gpu),
            "peakgpu" => shown.OrderByDescending(t => t.PeakGpu),
            _ => shown.OrderByDescending(t => t.Cpu),
        };
        var rows = shown.Select(t => new OptionItem
        {
            Name = t.Name,
            Description = $"CPU {t.Cpu:0.0}% (peak {t.PeakCpu:0}%) · {FormatMB(t.RamMB)} (peak {FormatMB(t.PeakRamMB)})" + (t.PeakGpu >= 1 ? $" · GPU {t.Gpu:0.0}% (peak {t.PeakGpu:0}%)" : ""),
            Note = sort switch
            {
                "peakcpu" => $"{t.PeakCpu:0}%", "mem" => FormatMB(t.RamMB), "peakmem" => FormatMB(t.PeakRamMB),
                "gpu" => $"{t.Gpu:0.0}%", "peakgpu" => $"{t.PeakGpu:0}%", _ => $"{t.Cpu:0.0}%",
            },
        }).ToList();
        AppListTitle.Text = (_source != null ? "THIS PC'S APPS (A WATCHED PC'S AREN'T SHOWN HERE) · " : "")
            + (_appTotals.Count == 0 ? "NOTHING RECORDED IN THIS RANGE" : $"{rows.Count} OF {_appTotals.Count} APPS · AVERAGES WHILE RECORDED");
        AppHistoryList.ItemsSource = rows;
    }

    private void AppHistoryRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptionItem { Name: { Length: > 0 } app } }) return;
        _appSelected = app;
        ShowApp();
    }

    private string SlotWord => _apps.SlotSeconds >= 60 ? $"{_apps.SlotSeconds / 60}-minute" : $"{_apps.SlotSeconds}-second";

    private void ShowApp()
    {
        var total = _appSelected == null ? null : _appTotals.Find(t => string.Equals(t.Name, _appSelected, StringComparison.OrdinalIgnoreCase));
        AppExportButton.IsEnabled = total != null;
        foreach (var chart in new[] { AppCpuChart, AppMemChart, AppGpuChart }) DrawAppChart(chart);
        if (total == null)
        {
            AppTitle.Text = _appSelected == null ? "Pick an app on the left" : $"{_appSelected} wasn't recorded in this range";
            AppSummary.Text = AppGapNote.Text = "";
            AppSessionsList.ItemsSource = null;
            AppSessionsTitle.Visibility = Visibility.Collapsed;
            return;
        }

        AppTitle.Text = total.Name;
        // Its share of what the whole PC used, over the slots it was recorded in.
        var mine = _apps.Apps.Where(a => string.Equals(a.Name, total.Name, StringComparison.OrdinalIgnoreCase)).Select(a => a.Time).ToHashSet();
        var during = _apps.Slots.Where(s => mine.Contains(s.Time)).ToList();
        double pcCpu = during.Count > 0 ? during.Average(s => Z(s.Cpu)) : 0, pcGpu = during.Count > 0 ? during.Average(s => Z(s.Gpu)) : 0;
        double pcMemMB = during.Count > 0 ? during.Average(s => Z(s.Mem)) / 100 * TotalRamMB : 0;
        static string Share(double part, double whole) => whole > 0 ? $" — {Math.Min(100, part / whole * 100):0}% of what the PC used then" : "";
        AppSummary.Text = $"Recorded in {Length(TimeSpan.FromSeconds(total.Slots * (double)_apps.SlotSeconds))} of this range ({SlotWord} slots)"
            + (total.Processes > 0 ? $", up to {total.Processes} process{(total.Processes == 1 ? "" : "es")} at once" : "") + ". While recorded:\n"
            + $"CPU {total.Cpu:0.0}% on average, peak {total.PeakCpu:0}%{Share(total.Cpu, pcCpu)}\n"
            + $"Memory {FormatMB(total.RamMB)} on average, peak {FormatMB(total.PeakRamMB)}{Share(total.RamMB, pcMemMB)}\n"
            + $"GPU {total.Gpu:0.0}% on average, peak {total.PeakGpu:0}%{Share(total.Gpu, pcGpu)}";
        AppGapNote.Text = $"{_appsFrom:ddd d MMM HH:mm} → {_appsTo:ddd d MMM HH:mm}. " + "Charts show each column's peak. A dotted line on the floor: the app wasn't in those snapshots — closed, or using under "
            + $"{ProcessMonitor.HistoryMinCpu}% CPU, {ProcessMonitor.HistoryMinGpu}% GPU and {ProcessMonitor.HistoryMinRamMB:0} MB "
            + "(before 1.17 only the 8 busiest by CPU and 5 by memory and GPU were recorded, so older days have more of these). Shaded: nothing was recorded — the PC was off or asleep, or Sysoptimizer wasn't running.";

        var sessions = HistoryStore.AppSessions(_apps, total.Name);
        AppSessionsTitle.Visibility = Visibility.Visible;
        AppSessionsTitle.Text = $"SESSIONS · {sessions.Count}" + (sessions.Count > 100 ? " (THE LATEST 100)" : "");
        AppSessionsList.ItemsSource = sessions.AsEnumerable().Reverse().Take(100).Select(s => new OptionItem
        {
            Name = $"{s.Start.ToLocalTime():ddd d MMM HH:mm} → {s.End.ToLocalTime():HH:mm}" + (s.End.ToLocalTime().Date != s.Start.ToLocalTime().Date ? $" ({s.End.ToLocalTime():ddd d MMM})" : ""),
            Description = $"CPU {s.Cpu:0.0}% (peak {s.PeakCpu:0}%) · {FormatMB(s.RamMB)} (peak {FormatMB(s.PeakRamMB)})" + (s.PeakGpu >= 1 ? $" · GPU {s.Gpu:0.0}% (peak {s.PeakGpu:0}%)" : "")
                + (s.Processes > 1 ? $" · up to {s.Processes} processes" : ""),
            Note = Length(s.End - s.Start),
        }).ToList();
    }

    private static float Z(float v) => float.IsNaN(v) ? 0 : v;
    private static readonly double TotalRamMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;

    private static Func<AppPoint, float> AppPeak(string metric) => metric switch { "mem" => a => a.PeakRamMB, "gpu" => a => a.PeakGpu, _ => a => a.PeakCpu };

    /// <summary>
    /// One of the three charts: the app's peak per column, in the History chart's plateaus and glow. Columns
    /// with snapshots but without the app get a dotted floor ("not recorded"); columns with nothing recorded, a shade.
    /// </summary>
    private void DrawAppChart(Canvas c)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        string metric = (string)c.Tag;
        string name = metric switch { "mem" => "MEMORY", "gpu" => "GPU", _ => "CPU" };
        Place(c, Label(name, 11), 12, 8);
        if (w <= 0 || h <= 0 || _appSelected == null || _appsTo <= _appsFrom) return;

        double top = 30, bottom = h - 8, plotH = bottom - top;
        var fromUtc = _appsFrom.ToUniversalTime();
        double span = (_appsTo - _appsFrom).TotalSeconds;
        var cols = MakeColumns(fromUtc, span, w, Math.Max(1, (int)Math.Min(w / 4, span / _apps.SlotSeconds)));
        int Col(DateTime t) => (int)((t - cols.Origin).TotalSeconds / cols.Seconds);
        var recorded = new bool[cols.Count];
        foreach (var s in _apps.Slots) if (Col(s.Time) is int k && k >= 0 && k < cols.Count && s.Snapshots > 0) recorded[k] = true;
        var values = new float[cols.Count];
        Array.Fill(values, float.NaN);
        var peak = AppPeak(metric);
        foreach (var a in _apps.Apps)
            if (string.Equals(a.Name, _appSelected, StringComparison.OrdinalIgnoreCase) && Col(a.Time) is int k && k >= 0 && k < cols.Count)
                values[k] = float.IsNaN(values[k]) ? peak(a) : Math.Max(values[k], peak(a));

        double max = values.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max();
        double scale = metric == "mem" ? NiceCeiling(max) : Math.Min(100, NiceCeiling(Math.Max(1, max)));
        double Y(double v) => bottom - Math.Clamp(v / scale, 0, 1) * plotH;
        string Format(double v) => metric == "mem" ? FormatMB(v) : $"{v:0.#}%";
        var topLabel = Label(Format(scale), 9);
        topLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(c, topLabel, w - topLabel.DesiredSize.Width - 6, top - 13);

        // The gaps, merged into runs: shaded where nothing was recorded, a dotted floor where the app wasn't in the list.
        int i = 0;
        while (i < cols.Count)
        {
            bool none = !recorded[i], missing = recorded[i] && float.IsNaN(values[i]);
            if (!none && !missing) { i++; continue; }
            int j = i;
            while (j < cols.Count && (!recorded[j]) == none && (recorded[j] && float.IsNaN(values[j])) == missing) j++;
            double x0 = Math.Max(0, cols.Left(i)), x1 = Math.Min(w, cols.Left(j));
            if (cols.Origin.AddSeconds(i * cols.Seconds) > DateTime.UtcNow) break; // the future isn't a gap
            if (x1 - x0 >= 1)
            {
                if (none)
                {
                    var shade = Place(c, new Rectangle { Width = x1 - x0, Height = plotH, Opacity = 0.06, ToolTip = "Nothing recorded: the PC was off or asleep, or Sysoptimizer wasn't running" }, x0, top);
                    shade.SetResourceReference(Shape.FillProperty, "TextPrimary");
                }
                else
                {
                    var dotted = new Line { X1 = x0, X2 = x1, Y1 = bottom - 1, Y2 = bottom - 1, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 1, 3 }, Opacity = 0.55,
                        ToolTip = "Not recorded: closed, or under the recording threshold — not measured as 0" };
                    dotted.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
                    c.Children.Add(dotted);
                }
            }
            i = j;
        }

        var accent = ThemeColor(metric switch { "mem" => "AccentMem", "gpu" => "AccentGpu", _ => "AccentCpu" }, Colors.SteelBlue);
        var fill = new LinearGradientBrush(Alpha(accent, 0x55), Alpha(accent, 0x00), 90);
        c.Children.Add(new Path { Data = SmoothRuns(values, cols, Y, baseline: bottom), Fill = fill, IsHitTestVisible = false });
        var line = new Path
        {
            Data = SmoothRuns(values, cols, Y, baseline: null), Stroke = new SolidColorBrush(accent), StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
        };
        line.SetResourceReference(UIElement.EffectProperty, metric switch { "mem" => "LineGlowMem", "gpu" => "LineGlowGpu", _ => "LineGlowCpu" });
        c.Children.Add(line);
        c.DataContext = (cols, values, recorded, (Func<double, string>)Format);
    }

    /// <summary>The value under the mouse, as the chart's tooltip.</summary>
    private void AppChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas { DataContext: (Columns cols, float[] values, bool[] recorded, Func<double, string> format) } c) return;
        int k = (int)Math.Floor((e.GetPosition(c).X - cols.Offset) / cols.Step);
        if (k < 0 || k >= values.Length) return;
        var at = cols.Origin.AddSeconds(k * cols.Seconds).ToLocalTime();
        c.ToolTip = $"{at:ddd d MMM HH:mm} · " + (!float.IsNaN(values[k]) ? $"peak {format(values[k])}" : recorded[k] ? "not recorded" : "nothing recorded");
    }

    // --- export: one app (CSV + JSON summary) or every app (a zip), for sending to a developer ---

    private void AppExport_Click(object sender, RoutedEventArgs e)
    {
        if (_appSelected == null || _appTotals.Find(t => string.Equals(t.Name, _appSelected, StringComparison.OrdinalIgnoreCase)) is not { } total) return;
        if (MessageBox.Show(this, $"This saves {total.Name}'s CPU, memory, GPU and process count from {_appsFrom:d MMM HH:mm} to {_appsTo:d MMM HH:mm} (gaps marked as not recorded), its sessions, the PC's overall use, and this PC's Windows version, CPU, RAM and graphics card — no other app, no user name and no file paths.",
                "Export app history", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"{SafeFileName(total.Name)} {_appsFrom:yyyy-MM-dd HHmm} to {_appsTo:yyyy-MM-dd HHmm}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        string json = System.IO.Path.ChangeExtension(dialog.FileName, ".json");
        try
        {
            File.WriteAllText(dialog.FileName, HistoryStore.AppCsv(_apps, total.Name), new UTF8Encoding(true));
            File.WriteAllText(json, JsonSerializer.Serialize(AppSummaryJson(_apps, new[] { total }, _appsFrom, _appsTo), JsonOptions));
            Log($"Exported {total.Name}'s history to {dialog.FileName} and {System.IO.Path.GetFileName(json)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Export failed — {ex.Message}"); }
    }

    private void AppExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_appTotals.Count == 0) { Log("Nothing recorded in this range to export."); return; }
        if (MessageBox.Show(this, $"This saves every app's CPU, memory, GPU and process count from {_appsFrom:d MMM HH:mm} to {_appsTo:d MMM HH:mm} ({_appTotals.Count} apps, one CSV each, gaps marked as not recorded), their sessions, the PC's overall use, and this PC's Windows version, CPU, RAM and graphics card — no user name and no file paths.",
                "Export all apps", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var dialog = new SaveFileDialog { Filter = "Zip|*.zip", FileName = $"Sysoptimizer apps {_appsFrom:yyyy-MM-dd HHmm} to {_appsTo:yyyy-MM-dd HHmm}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using (var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create))
            {
                foreach (var t in _appTotals)
                    using (var w = new StreamWriter(zip.CreateEntry($"{SafeFileName(t.Name)}.csv").Open(), new UTF8Encoding(true)))
                        w.Write(HistoryStore.AppCsv(_apps, t.Name));
                using var summary = new StreamWriter(zip.CreateEntry("summary.json").Open());
                summary.Write(JsonSerializer.Serialize(AppSummaryJson(_apps, _appTotals, _appsFrom, _appsTo), JsonOptions));
            }
            Log($"Exported {_appTotals.Count} apps' history to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Export failed — {ex.Message}"); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string SafeFileName(string name) => string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>The JSON beside the CSV: the range, each app's totals and sessions, and what this PC is.</summary>
    private object AppSummaryJson(AppHistory h, IEnumerable<AppTotal> apps, DateTime from, DateTime to) => new
    {
        exported = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
        from = new DateTimeOffset(from).ToString("o", CultureInfo.InvariantCulture),
        to = new DateTimeOffset(to).ToString("o", CultureInfo.InvariantCulture),
        slot_seconds = h.SlotSeconds,
        recording = $"Every 10 s (2 s while CPU or GPU is over 80%), apps over {ProcessMonitor.HistoryMinCpu}% CPU, {ProcessMonitor.HistoryMinGpu}% GPU or {ProcessMonitor.HistoryMinRamMB} MB, plus the 15 heaviest by each; before 1.17 only the 8 busiest by CPU and 5 by memory and GPU. Averages are over the snapshots an app was in.",
        system = SystemContext(),
        apps = apps.Select(t => new
        {
            name = PrivateText(t.Name),
            cpu_pct = Math.Round(t.Cpu, 2), cpu_peak_pct = t.PeakCpu, memory_mb = Math.Round(t.RamMB), memory_peak_mb = t.PeakRamMB,
            gpu_pct = Math.Round(t.Gpu, 2), gpu_peak_pct = t.PeakGpu, recorded_minutes = Math.Round(t.Slots * h.SlotSeconds / 60.0, 1), max_processes = t.Processes,
            sessions = HistoryStore.AppSessions(h, t.Name).Select(s => new
            {
                start = new DateTimeOffset(s.Start.ToLocalTime()).ToString("o", CultureInfo.InvariantCulture),
                end = new DateTimeOffset(s.End.ToLocalTime()).ToString("o", CultureInfo.InvariantCulture),
                cpu_pct = Math.Round(s.Cpu, 2), cpu_peak_pct = s.PeakCpu, memory_mb = Math.Round(s.RamMB), memory_peak_mb = s.PeakRamMB,
                gpu_pct = Math.Round(s.Gpu, 2), gpu_peak_pct = s.PeakGpu, max_processes = s.Processes,
            }),
        }),
    };

    /// <summary>What a developer needs to know about this PC, and nothing that names its user.</summary>
    private static object SystemContext()
    {
        string? Reg(string key, string value) => Registry.GetValue(key, value, null)?.ToString();
        const string Nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var gpus = new List<object>();
        int? cores = null;
        try
        {
            using (var cpu = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                cores = cpu.Get().Cast<System.Management.ManagementObject>().Sum(o => Convert.ToInt32(o["NumberOfCores"]));
            using var video = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var o in video.Get().Cast<System.Management.ManagementObject>())
                gpus.Add(new { name = PrivateText(o["Name"]?.ToString() ?? ""), driver = o["DriverVersion"]?.ToString() });
        }
        catch (System.Management.ManagementException) { } // WMI broken on this PC: the rest still says enough
        return new
        {
            // Windows 11 still says "Windows 10" in ProductName; the build tells them apart.
            windows = $"{(int.TryParse(Reg(Nt, "CurrentBuild"), out int build) && build >= 22000 ? Reg(Nt, "ProductName")?.Replace("Windows 10", "Windows 11") : Reg(Nt, "ProductName"))} {Reg(Nt, "DisplayVersion")}".Trim(),
            windows_build = $"{Reg(Nt, "CurrentBuild")}.{Reg(Nt, "UBR")}",
            cpu = PrivateText(Reg(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? ""),
            cpu_cores = cores,
            cpu_threads = Environment.ProcessorCount,
            ram_gb = Math.Round(TotalRamMB / 1024, 1),
            gpus,
            sysoptimizer = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3),
        };
    }

    /// <summary>The user's folder as %USERPROFILE%, their name as %USERNAME%: nothing in an export says who they are.</summary>
    internal static string PrivateText(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        return user.Length >= 3 ? text.Replace(user, "%USERNAME%", StringComparison.OrdinalIgnoreCase) : text;
    }
}
