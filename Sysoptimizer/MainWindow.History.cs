using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;
using Line = System.Windows.Shapes.Line;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Sysoptimizer;

/// <summary>
/// The History tab, modelled on AppControl's timeline: one stepped, heat-coloured chart of the chosen
/// metric; numbered badges where apps were launched; sleep shaded, power-off and not-recording gaps drawn
/// as such; a temperature band underneath; and an overview strip of the whole retained history to drag
/// through. Up to 24 h it draws the per-second data, beyond that the per-minute peaks.
/// </summary>
public partial class MainWindow
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const double BadgeRow = 28, TempBand = 18;

    private double _historyHours = 1;
    private DateTime? _historyEnd; // null = follow "now"
    private string _historyMetric = "cpu";
    private DateTime _historyFrom, _historyTo;
    private bool _historyMinutely, _historyLoading;
    private HistoryData _hist = new(new(), new(), new());
    private List<DateTime> _histProcTimes = new();
    private Dictionary<DateTime, List<ProcSample>> _histProcs = new();
    private Line? _cursorLine;

    private int _navDays = 3;
    private List<SysSample> _navData = new();
    private DateTime _navFrom, _navTo, _navLoadedAt;
    private bool _navDragging;
    private Border? _navSelection;

    private void InitHistory()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            HistoryStore.RetentionDays = key?.GetValue("RetentionDays") is int days ? days : 90;
        RetentionCombo.SelectedItem = RetentionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == HistoryStore.RetentionDays.ToString()) ?? RetentionCombo.Items[1];

        HistoryChart.SizeChanged += (_, _) => DrawHistory();
        HistoryNavigator.SizeChanged += (_, _) => DrawNavigator();
        ThemeManager.ThemeChanged += () => { DrawHistory(); DrawNavigator(); }; // the heat gradients are built from theme colours
    }

    private async Task LoadHistory()
    {
        if (_historyLoading) return;
        _historyLoading = true;
        try
        {
            var to = _historyEnd ?? DateTime.Now;
            var from = to.AddHours(-_historyHours);
            bool minutely = _historyHours > 24;
            var data = await Task.Run(() => minutely ? HistoryStore.ReadMinutes(from, to) : HistoryStore.Read(from, to));

            // The overview re-reads days at a time — only when it's stale, its span changed, or the window left it.
            var navFrom = _navDays == 0 ? (HistoryStore.OldestDay() ?? DateTime.Now.Date) : DateTime.Now.AddDays(-_navDays);
            if (from < navFrom) navFrom = from;
            if ((DateTime.Now - _navLoadedAt).TotalSeconds > 60 || Math.Abs((navFrom - _navFrom).TotalMinutes) > 1)
            {
                var navTo = DateTime.Now;
                _navData = await Task.Run(() => HistoryStore.ReadMinutes(navFrom, navTo, systemOnly: true).System);
                (_navFrom, _navTo, _navLoadedAt) = (navFrom, navTo, DateTime.Now);
            }

            (_historyFrom, _historyTo, _historyMinutely, _hist) = (from, to, minutely, data);
            _histProcs = data.Processes.GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.ToList());
            _histProcTimes = _histProcs.Keys.OrderBy(t => t).ToList();

            HistoryRangeText.Text = $"{from:ddd d MMM HH:mm} – {to:ddd d MMM HH:mm}" + (data.System.Count == 0 ? " · nothing recorded" : "")
                                    + (minutely ? " · per-minute peaks (click to zoom in to every second)" : "");
            HistoryRangeTitle.Text = $"TOP APPS IN THIS RANGE (AVERAGE {MetricLabel().ToUpperInvariant()})";
            HistoryRangeList.ItemsSource = HistoryStore.TopProcesses(data.Processes, SortKey()).Select(t => new OptionItem
            {
                Name = t.Name,
                Description = $"CPU peak {t.PeakCpu:0}% · {FormatMB(t.PeakRamMB)} peak" + (t.PeakGpu >= 1 ? $" · GPU peak {t.PeakGpu:0}%" : ""),
                Note = _historyMetric switch { "mem" => FormatMB(t.AvgRamMB), "gpu" => $"{t.AvgGpu:0.0}% GPU", _ => $"{t.AvgCpu:0.0}% CPU" },
            }).ToList();

            DrawHistory();
            DrawNavigator();
        }
        finally { _historyLoading = false; }
    }

    private static string FormatMB(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    private string MetricLabel() => _historyMetric switch { "mem" => "Memory", "gpu" => "GPU", _ => "CPU" };
    private string SortKey() => _historyMetric switch { "mem" => "ram", "gpu" => "gpu", _ => "cpu" };
    private float Metric(SysSample s) => _historyMetric switch { "mem" => s.Mem, "gpu" => s.Gpu, _ => s.Cpu };
    private float MetricTemp(SysSample s) => _historyMetric == "gpu" ? s.GpuTemp : s.CpuTemp;

    private Color ThemeColor(string key, Color fallback) => (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
    private Color MetricColor() => ThemeColor(_historyMetric switch { "mem" => "AccentMem", "gpu" => "AccentGpu", _ => "AccentCpu" }, Colors.SteelBlue);
    private static readonly Color Hot = Color.FromRgb(0xEF, 0x44, 0x44), Warm = Color.FromRgb(0xF9, 0x73, 0x16);
    private static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private TextBlock Label(string text, double size = 10, string brush = "TextSecondary")
    {
        var t = new TextBlock { Text = text, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static T Place<T>(Canvas canvas, T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
        return element;
    }

    // --- main chart ---

    private void DrawHistory()
    {
        var c = HistoryChart;
        c.Children.Clear();
        _cursorLine = null;
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w <= 0 || h <= 0 || _historyTo <= _historyFrom) return;

        double top = BadgeRow, bottom = h - TempBand - 6, plotH = bottom - top;
        var fromUtc = _historyFrom.ToUniversalTime();
        double span = (_historyTo - _historyFrom).TotalSeconds;
        double X(DateTime utc) => (utc - fromUtc).TotalSeconds / span * w;
        double Y(double v) => bottom - Math.Clamp(v, 0, 100) / 100 * plotH;

        foreach (var (v, text) in new[] { (100.0, "100%"), (50.0, "50%"), (0.0, "0%") })
        {
            var grid = new Line { X1 = 0, X2 = w, Y1 = Y(v), Y2 = Y(v), StrokeThickness = 1, Opacity = 0.15 };
            grid.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
            c.Children.Add(grid);
            Place(c, Label(text, 9), w - 30, Y(v) - (v == 100 ? -1 : 13));
        }

        var samples = _hist.System;
        int buckets = Math.Max(1, (int)(w / 2));
        double bucketSec = span / buckets;
        double gapLimit = Math.Max(_historyMinutely ? 150 : 5, bucketSec * 1.5);

        // Gaps: shaded for sleep, a dashed baseline with a power icon when the PC was off, dashed alone
        // when Sysoptimizer simply wasn't running. Includes the stretch before the first sample.
        var gaps = new List<(DateTime A, DateTime B)>();
        var previous = fromUtc;
        foreach (var s in samples)
        {
            if ((s.Time - previous).TotalSeconds > gapLimit) gaps.Add((previous, s.Time));
            previous = s.Time;
        }
        var end = (_historyEnd == null ? DateTime.UtcNow : _historyTo.ToUniversalTime());
        if ((end - previous).TotalSeconds > gapLimit) gaps.Add((previous, end));
        foreach (var (a, b) in gaps) DrawGap(c, X(a), X(b), top, bottom, ClassifyGap(a, b));

        // The metric, as steps of each 2px column's peak: a spike survives any zoom level.
        var peaks = Bucket(samples, Metric, fromUtc, span, buckets);
        var accent = MetricColor();
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            bool open = false;
            double lastX = 0;
            for (int i = 0; i <= buckets; i++)
            {
                float v = i < buckets ? peaks[i] : float.NaN;
                double x0 = i * w / buckets, x1 = (i + 1) * w / buckets;
                if (float.IsNaN(v))
                {
                    if (open) { ac.LineTo(new Point(lastX, bottom), false, false); open = false; }
                    continue;
                }
                double y = Y(v);
                if (!open)
                {
                    lc.BeginFigure(new Point(x0, y), false, false);
                    ac.BeginFigure(new Point(x0, bottom), true, true);
                    ac.LineTo(new Point(x0, y), false, false);
                    open = true;
                }
                else
                {
                    lc.LineTo(new Point(x0, y), true, false);
                    ac.LineTo(new Point(x0, y), false, false);
                }
                lc.LineTo(new Point(x1, y), true, false);
                ac.LineTo(new Point(x1, y), false, false);
                lastX = x1;
            }
        }
        // Colour follows height: the theme accent at rest, orange from ~70%, red near 100%.
        LinearGradientBrush Heat(byte hotA, byte warmA, byte restA, byte floorA) => new(new GradientStopCollection
        {
            new(Alpha(Hot, hotA), 0), new(Alpha(Warm, warmA), 0.3), new(Alpha(accent, restA), 0.5), new(Alpha(accent, floorA), 1),
        }, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute };
        c.Children.Add(new Path { Data = area, Fill = Heat(0x70, 0x55, 0x40, 0x06) });
        c.Children.Add(new Path { Data = line, Stroke = Heat(0xFF, 0xFF, 0xFF, 0xFF), StrokeThickness = 1.5 });

        DrawTemperatureBand(c, w, h - TempBand / 2 - 3, fromUtc, span, buckets);
        DrawLaunchBadges(c, X, w, bottom);

        if (_historyEnd == null)
        {
            double nx = Math.Min(w - 1, X(DateTime.UtcNow));
            c.Children.Add(new Line { X1 = nx, X2 = nx, Y1 = 4, Y2 = bottom, Stroke = new SolidColorBrush(Hot), StrokeThickness = 1.5 });
            Place(c, new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Hot) }, nx - 3.5, 1);
        }

        _cursorLine = new Line { Y1 = top - 4, Y2 = bottom, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _cursorLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
        c.Children.Add(_cursorLine);

        DrawAxis(w);
    }

    /// <summary>Peak of each time column; NaN where nothing was recorded.</summary>
    private static float[] Bucket(List<SysSample> samples, Func<SysSample, float> value, DateTime fromUtc, double span, int buckets)
    {
        var peaks = new float[buckets];
        Array.Fill(peaks, float.NaN);
        foreach (var s in samples)
        {
            int b = (int)((s.Time - fromUtc).TotalSeconds / span * buckets);
            float v = value(s);
            if (b < 0 || b >= buckets || float.IsNaN(v)) continue;
            peaks[b] = float.IsNaN(peaks[b]) ? v : Math.Max(peaks[b], v);
        }
        return peaks;
    }

    private enum GapKind { NotRecording, Sleep, PoweredOff }

    private GapKind ClassifyGap(DateTime a, DateTime b)
    {
        var ev = _hist.Events;
        if (ev.Any(e => e.Kind == "sleep" && e.Time >= a.AddSeconds(-120) && e.Time <= a.AddSeconds(30))
            || ev.Any(e => e.Kind == "wake" && e.Time >= b.AddSeconds(-30) && e.Time <= b.AddSeconds(120)))
            return GapKind.Sleep;
        if (ev.Any(e => e.Kind == "start" && e.Time >= b.AddSeconds(-30) && e.Time <= b.AddMinutes(10)
                        && long.TryParse(e.Detail, out long boot) && DateTimeOffset.FromUnixTimeSeconds(boot).UtcDateTime > a))
            return GapKind.PoweredOff;
        return GapKind.NotRecording;
    }

    private void DrawGap(Canvas c, double x0, double x1, double top, double bottom, GapKind kind)
    {
        double width = x1 - x0;
        if (width < 1) return;
        if (kind == GapKind.Sleep)
        {
            var shade = Place(c, new Rectangle { Width = width, Height = bottom - top + BadgeRow - 4, Opacity = 0.07 }, x0, 4);
            shade.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextPrimary");
            if (width > 22) Place(c, IconLabel("", "Asleep"), x1 - 18, 8); // moon
            return;
        }
        var dashed = new Line { X1 = x0, X2 = x1, Y1 = bottom, Y2 = bottom, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.6 };
        dashed.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
        c.Children.Add(dashed);
        if (kind == GapKind.PoweredOff && width > 24) Place(c, IconLabel("", "PC off or restarting"), (x0 + x1) / 2 - 6, bottom - 18); // power
    }

    private TextBlock IconLabel(string glyph, string tip)
    {
        var t = Label(glyph, 12);
        t.FontFamily = new FontFamily(IconFont);
        t.ToolTip = tip;
        return t;
    }

    /// <summary>A band under the chart whose thickness and colour follow the temperature (CPU, or GPU on the GPU view).</summary>
    private void DrawTemperatureBand(Canvas c, double w, double centerY, DateTime fromUtc, double span, int buckets)
    {
        var temps = Bucket(_hist.System, MetricTemp, fromUtc, span, buckets);
        if (temps.All(float.IsNaN)) return;
        // 70 °C is an ordinary working temperature for a desktop CPU — only genuinely hot stretches should shout.
        double Thick(float t) => Math.Clamp((t - 35) / 60 * 9, 1.5, 9);
        var accent = MetricColor();
        Color Shade(float t) => t >= 85 ? Hot : t >= 75 ? Warm : t >= 60 ? Color.FromRgb(0xC9, 0x8B, 0x7A) : accent;

        var geometry = new StreamGeometry();
        var stops = new GradientStopCollection();
        using (var g = geometry.Open())
        {
            int i = 0;
            while (i < buckets)
            {
                if (float.IsNaN(temps[i])) { i++; continue; }
                int j = i;
                while (j < buckets && !float.IsNaN(temps[j])) j++;
                // One closed outline per recorded run: along the top edge, back along the bottom.
                g.BeginFigure(new Point(i * w / buckets, centerY - Thick(temps[i]) / 2), true, true);
                for (int k = i; k < j; k++) g.LineTo(new Point((k + 0.5) * w / buckets, centerY - Thick(temps[k]) / 2), false, false);
                for (int k = j - 1; k >= i; k--) g.LineTo(new Point((k + 0.5) * w / buckets, centerY + Thick(temps[k]) / 2), false, false);
                for (int k = i; k < j; k += 3) stops.Add(new GradientStop(Alpha(Shade(temps[k]), 0xD0), (k + 0.5) / buckets));
                i = j;
            }
        }
        var band = new Path { Data = geometry, Fill = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)) };
        band.ToolTip = "Temperature: thicker and redder is hotter";
        c.Children.Add(band);
    }

    /// <summary>Numbered badges along the top: how many apps were opened there, grouped when they'd overlap.</summary>
    private void DrawLaunchBadges(Canvas c, Func<DateTime, double> x, double w, double bottom)
    {
        var launches = _hist.Events.Where(e => e.Kind == "launch").OrderBy(e => e.Time).ToList();
        var groups = new List<List<HistoryEvent>>();
        foreach (var e in launches)
        {
            if (groups.Count == 0 || x(e.Time) - x(groups[^1][0].Time) > 26) groups.Add(new());
            groups[^1].Add(e);
        }
        var accent = MetricColor();
        foreach (var g in groups)
        {
            double gx = Math.Clamp(x(g[0].Time), 10, w - 10);
            c.Children.Add(new Line { X1 = gx, X2 = gx, Y1 = 20, Y2 = bottom, Stroke = new SolidColorBrush(Alpha(accent, 0x50)), StrokeThickness = 1, IsHitTestVisible = false });
            var badge = new Border
            {
                Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1), MinWidth = 20,
                Child = new TextBlock { Text = g.Count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center },
                ToolTip = string.Join("\n", g.Take(20).Select(e => $"{e.Time.ToLocalTime():HH:mm:ss}  {e.Detail}")) + (g.Count > 20 ? $"\n… and {g.Count - 20} more" : ""),
            };
            AutomationPropertiesName(badge, $"{g.Count} apps launched");
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(c, badge, gx - badge.DesiredSize.Width / 2, 2);
        }
    }

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>Time labels at round steps — 10 min, 1 h, 3 h, 12 h, a day — whatever keeps about eight across.</summary>
    private void DrawAxis(double w)
    {
        HistoryAxis.Children.Clear();
        double span = (_historyTo - _historyFrom).TotalSeconds;
        int[] steps = { 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800 };
        int step = steps.FirstOrDefault(s => span / s <= 8, 604800);
        var t = _historyFrom.Date.AddSeconds(Math.Ceiling((_historyFrom - _historyFrom.Date).TotalSeconds / step) * step);
        for (; t <= _historyTo; t = t.AddSeconds(step))
        {
            double x = (t - _historyFrom).TotalSeconds / span * w;
            string text = step >= 86400 || t.TimeOfDay == TimeSpan.Zero ? t.ToString("ddd d") : t.ToString("HH:mm");
            var label = Label(text);
            label.Width = 60;
            label.TextAlignment = TextAlignment.Center;
            Place(HistoryAxis, label, Math.Clamp(x - 30, 0, w - 60), 0);
        }
    }

    // --- hover / click ---

    private void HistoryChart_MouseMove(object sender, MouseEventArgs e)
    {
        double w = HistoryChart.ActualWidth;
        if (w <= 0 || _historyTo <= _historyFrom) return;
        double x = Math.Clamp(e.GetPosition(HistoryChart).X, 0, w);
        if (_cursorLine != null) { _cursorLine.X1 = _cursorLine.X2 = x; _cursorLine.Visibility = Visibility.Visible; }
        double span = (_historyTo - _historyFrom).TotalSeconds;
        var time = _historyFrom.ToUniversalTime().AddSeconds(x / w * span);
        double tolerance = Math.Max(_historyMinutely ? 90 : 3, span / w * 3);

        string Val(float v, string u) => float.IsNaN(v) ? "—" : $"{v:0}{u}";
        if (_hist.System.Count > 0 && Nearest(_hist.System, time, s => s.Time) is var s && Math.Abs((s.Time - time).TotalSeconds) <= tolerance)
            HistoryCursorTitle.Text = $"{s.Time.ToLocalTime():ddd HH:mm:ss}{(_historyMinutely ? " (minute peak)" : "")} — CPU {Val(s.Cpu, "%")} · MEM {Val(s.Mem, "%")} · GPU {Val(s.Gpu, "%")} · {Val(s.CpuTemp, "°")} / {Val(s.GpuTemp, "°")}";
        else
            HistoryCursorTitle.Text = $"{time.ToLocalTime():ddd HH:mm:ss} — nothing recorded";

        if (_histProcTimes.Count == 0) { HistoryCursorList.ItemsSource = null; return; }
        var at = Nearest(_histProcTimes, time, t => t);
        if (Math.Abs((at - time).TotalSeconds) > Math.Max(60, tolerance)) { HistoryCursorList.ItemsSource = null; return; }
        HistoryCursorList.ItemsSource = _histProcs[at].OrderByDescending(p => _historyMetric switch { "mem" => p.RamMB, "gpu" => p.Gpu, _ => p.Cpu })
            .Select(p => new OptionItem
            {
                Name = p.Name,
                Description = FormatMB(p.RamMB) + (p.Gpu >= 1 ? $" · GPU {p.Gpu:0}%" : ""),
                Note = $"{p.Cpu:0.0}% CPU",
            }).ToList();
    }

    private void HistoryChart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_cursorLine != null) _cursorLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>Click = zoom to the hour around that moment, at full per-second resolution.</summary>
    private void HistoryChart_Click(object sender, MouseButtonEventArgs e)
    {
        double w = HistoryChart.ActualWidth;
        if (w <= 0 || _historyHours <= 1) return;
        var time = _historyFrom.AddSeconds(e.GetPosition(HistoryChart).X / w * (_historyTo - _historyFrom).TotalSeconds);
        var end = time.AddMinutes(30);
        _historyEnd = end >= DateTime.Now ? null : end;
        if (HistoryRange1h.IsChecked == true) _ = LoadHistory();
        else HistoryRange1h.IsChecked = true; // its Checked handler reloads
    }

    private static T Nearest<T>(List<T> sorted, DateTime time, Func<T, DateTime> timeOf)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (timeOf(sorted[mid]) < time) lo = mid + 1; else hi = mid;
        }
        return lo > 0 && (time - timeOf(sorted[lo - 1])) < (timeOf(sorted[lo]) - time) ? sorted[lo - 1] : sorted[lo];
    }

    // --- overview strip ---

    private void DrawNavigator()
    {
        var c = HistoryNavigator;
        c.Children.Clear();
        _navSelection = null;
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w <= 0 || h <= 0 || _navTo <= _navFrom) return;
        double span = (_navTo - _navFrom).TotalSeconds;
        var fromUtc = _navFrom.ToUniversalTime();
        double X(DateTime local) => (local - _navFrom).TotalSeconds / span * w;

        for (var day = _navFrom.Date.AddDays(1); day < _navTo; day = day.AddDays(1))
        {
            var tick = new Line { X1 = X(day), X2 = X(day), Y1 = 0, Y2 = h, StrokeThickness = 1, Opacity = 0.2 };
            tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
            c.Children.Add(tick);
            if (span / 86400 <= 14) Place(c, Label(day.ToString("ddd d"), 9), X(day) + 3, 1);
        }

        int buckets = Math.Max(1, (int)(w / 2));
        var peaks = Bucket(_navData, Metric, fromUtc, span, buckets);
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            bool open = false;
            double lastX = 0;
            for (int i = 0; i <= buckets; i++)
            {
                float v = i < buckets ? peaks[i] : float.NaN;
                if (float.IsNaN(v)) { if (open) { g.LineTo(new Point(lastX, h), false, false); open = false; } continue; }
                double x0 = i * w / buckets, x1 = (i + 1) * w / buckets, y = h - 2 - Math.Clamp(v, 0, 100) / 100 * (h - 12);
                if (!open) { g.BeginFigure(new Point(x0, h), true, true); open = true; }
                g.LineTo(new Point(x0, y), false, false);
                g.LineTo(new Point(x1, y), false, false);
                lastX = x1;
            }
        }
        c.Children.Add(new Path { Data = area, Fill = new SolidColorBrush(Alpha(MetricColor(), 0x60)), IsHitTestVisible = false });

        var accent = MetricColor();
        _navSelection = new Border
        {
            Width = Math.Max(6, X(_historyTo) - X(_historyFrom)), Height = h - 2,
            BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Alpha(accent, 0x22)), IsHitTestVisible = false,
        };
        Place(c, _navSelection, Math.Clamp(X(_historyFrom), 0, w - _navSelection.Width), 1);

        _navStartLabel = Label("", 9, "TextPrimary");
        _navEndLabel = Label("", 9, "TextPrimary");
        _navStartLabel.IsHitTestVisible = _navEndLabel.IsHitTestVisible = false;
        Place(c, _navStartLabel, 6, h - 15);
        _navEndLabel.Width = 120;
        _navEndLabel.TextAlignment = TextAlignment.Right;
        Place(c, _navEndLabel, w - 126, h - 15);
        UpdateNavigatorLabels();
    }

    private enum NavDrag { None, Move, Left, Right }
    private NavDrag _navDrag;
    private double _navGrabOffset;
    private const double EdgeGrip = 7, MinSelection = 8;

    private NavDrag HitTestSelection(double x)
    {
        if (_navSelection == null) return NavDrag.None;
        double left = Canvas.GetLeft(_navSelection), right = left + _navSelection.Width;
        if (Math.Abs(x - left) <= EdgeGrip) return NavDrag.Left;
        if (Math.Abs(x - right) <= EdgeGrip) return NavDrag.Right;
        return x > left && x < right ? NavDrag.Move : NavDrag.None;
    }

    private void Navigator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_navSelection == null) return;
        double x = e.GetPosition(HistoryNavigator).X;
        _navDrag = HitTestSelection(x);
        if (_navDrag == NavDrag.None)
        {
            // A click beside the box centres it there, then keeps dragging as a move.
            Canvas.SetLeft(_navSelection, ClampLeft(x - _navSelection.Width / 2, _navSelection.Width));
            _navDrag = NavDrag.Move;
        }
        _navGrabOffset = x - Canvas.GetLeft(_navSelection);
        _navDragging = HistoryNavigator.CaptureMouse();
    }

    private void Navigator_MouseMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(HistoryNavigator).X;
        if (!_navDragging || _navSelection == null)
        {
            HistoryNavigator.Cursor = HitTestSelection(x) is NavDrag.Left or NavDrag.Right ? Cursors.SizeWE : Cursors.Hand;
            return;
        }

        // Only the box moves while dragging; the chart reloads once, on release.
        double w = HistoryNavigator.ActualWidth;
        double left = Canvas.GetLeft(_navSelection), right = left + _navSelection.Width;
        switch (_navDrag)
        {
            case NavDrag.Move:
                Canvas.SetLeft(_navSelection, ClampLeft(x - _navGrabOffset, _navSelection.Width));
                break;
            case NavDrag.Left:
                double newLeft = Math.Clamp(x, 0, right - MinSelection);
                Canvas.SetLeft(_navSelection, newLeft);
                _navSelection.Width = right - newLeft;
                break;
            case NavDrag.Right:
                _navSelection.Width = Math.Clamp(x, left + MinSelection, w) - left;
                break;
        }
        UpdateNavigatorLabels();
    }

    private double ClampLeft(double left, double width) => Math.Clamp(left, 0, Math.Max(0, HistoryNavigator.ActualWidth - width));

    private void Navigator_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_navDragging || _navSelection == null) return;
        _navDragging = false;
        HistoryNavigator.ReleaseMouseCapture();

        var (from, to) = NavigatorSelectionTimes();
        if (_navDrag is NavDrag.Left or NavDrag.Right)
        {
            // A width no pill offers: the pills no longer describe the window, so none stays lit.
            _historyHours = Math.Max(1.0 / 60, (to - from).TotalHours);
            foreach (var pill in ((Panel)HistoryRange1h.Parent).Children.OfType<RadioButton>().Where(r => r.GroupName == "HistoryRange"))
                pill.IsChecked = false;
        }
        _historyEnd = to >= DateTime.Now.AddMinutes(-1) ? null : to;
        _navDrag = NavDrag.None;
        _ = LoadHistory();
    }

    private (DateTime From, DateTime To) NavigatorSelectionTimes()
    {
        double w = HistoryNavigator.ActualWidth, span = (_navTo - _navFrom).TotalSeconds;
        double left = Canvas.GetLeft(_navSelection!), right = left + _navSelection!.Width;
        return (_navFrom.AddSeconds(left / w * span), _navFrom.AddSeconds(right / w * span));
    }

    private TextBlock? _navStartLabel, _navEndLabel;

    /// <summary>The selection's own start and end, at the strip's two ends — like AppControl's.</summary>
    private void UpdateNavigatorLabels()
    {
        if (_navSelection == null || _navStartLabel == null || _navEndLabel == null) return;
        var (from, to) = NavigatorSelectionTimes();
        string fmt = (to - from).TotalHours < 24 ? (to.Date == DateTime.Today ? "HH:mm:ss" : "ddd HH:mm") : "ddd d MMM";
        _navStartLabel.Text = from.ToString(fmt);
        _navEndLabel.Text = to.ToString(fmt);
    }

    // --- controls ---

    private void HistoryRange_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string hours }) _historyHours = double.Parse(hours);
        if (IsLoaded) _ = LoadHistory();
    }

    private void HistoryMetric_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string metric }) _historyMetric = metric;
        if (IsLoaded) _ = LoadHistory();
    }

    private void HistoryPrev_Click(object sender, RoutedEventArgs e)
    {
        _historyEnd = (_historyEnd ?? DateTime.Now).AddHours(-_historyHours);
        _ = LoadHistory();
    }

    private void HistoryNext_Click(object sender, RoutedEventArgs e)
    {
        var next = (_historyEnd ?? DateTime.Now).AddHours(_historyHours);
        _historyEnd = next >= DateTime.Now ? null : next;
        _ = LoadHistory();
    }

    private void HistoryNow_Click(object sender, RoutedEventArgs e)
    {
        _historyEnd = null;
        _ = LoadHistory();
    }

    private void NavigatorSpan_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NavigatorSpanCombo.SelectedItem is ComboBoxItem { Tag: string days }) _navDays = int.Parse(days);
        _navLoadedAt = DateTime.MinValue;
        if (IsLoaded) _ = LoadHistory();
    }

    private void Retention_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RetentionCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        int days = int.Parse(tag);
        if (days == HistoryStore.RetentionDays) return;
        HistoryStore.RetentionDays = days;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer"))
            key.SetValue("RetentionDays", days, RegistryValueKind.DWord);
        Log($"History is now kept for {days} days (currently {HistoryStore.SizeMB():0} MB on disk). Older days are removed at midnight.");
    }
}
