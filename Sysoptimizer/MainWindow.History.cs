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
    private const double BadgeRow = 28, TempBand = 45;

    private double _historyHours = 1;
    private DateTime? _historyEnd; // null = follow "now"
    private string _historyMetric = "cpu";
    private DateTime _historyFrom, _historyTo;
    private bool _historyMinutely, _historyLoading;
    private HistoryData _hist = new(new(), new(), new());
    private List<DateTime> _histProcTimes = new();
    private Dictionary<DateTime, List<ProcSample>> _histProcs = new();
    private Line? _cursorLine;
    private Ellipse? _cursorDot;
    private Border? _cursorValue;
    private float[] _chartPeaks = Array.Empty<float>();
    private float[] _chartTemps = Array.Empty<float>();
    private Columns _chartCols;

    /// <summary>
    /// The chart's columns, laid on a fixed time grid: column k always covers the same absolute stretch of
    /// time (Origin + k·Seconds), however the view is scrolled. Measured from the view's left edge instead,
    /// every drag frame regrouped the samples into different columns and the curve shimmered; on the grid a
    /// drag only slides it sideways. Column k is drawn from Offset + k·Step pixels.
    /// </summary>
    private readonly record struct Columns(DateTime Origin, double Seconds, double Step, double Offset, int Count)
    {
        public double Center(int k) => Offset + (k + 0.5) * Step;
        public double Left(int k) => Offset + k * Step;
    }

    /// <summary>
    /// Column lengths come from a fixed ladder of round durations, so resizing the window (or zooming a
    /// little) keeps the same columns and only stretches them — a length straight from the width changed
    /// with every pixel, regrouping the samples and making the curve shimmer exactly like a drag once did.
    /// </summary>
    private static readonly int[] ColumnSeconds =
        { 1, 2, 3, 5, 10, 15, 20, 30, 60, 120, 180, 300, 600, 900, 1200, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };

    private static Columns MakeColumns(DateTime fromUtc, double span, double w, int buckets)
    {
        double raw = span / buckets;
        double seconds = raw < 1 ? raw : ColumnSeconds.FirstOrDefault(c => c >= raw, (int)Math.Ceiling(raw));
        buckets = (int)Math.Ceiling(span / seconds);
        long ticks = Math.Max(1, (long)(seconds * TimeSpan.TicksPerSecond));
        var origin = new DateTime(fromUtc.Ticks - fromUtc.Ticks % ticks, DateTimeKind.Utc);
        return new Columns(origin, seconds, seconds / span * w, (origin - fromUtc).TotalSeconds / span * w, buckets + 2);
    }
    private double _chartWidth;
    private Func<double, double> _chartY = v => 0;
    private bool _animateChart = true;
    private double? _hoverX;

    private double _navHours = 24;
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

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            if (key?.GetValue("NavigatorHours") is int savedSpan) _navHours = savedSpan;
        NavigatorSpanCombo.SelectedItem = NavigatorSpanCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _navHours.ToString()) ?? NavigatorSpanCombo.SelectedItem;

        HistoryChart.SizeChanged += (_, _) => DrawHistory();
        HistoryChart.LostMouseCapture += (_, _) => { if (_pressX != null) CancelSelection(); EndPan(); }; // capture taken away mid-press
        HistoryNavigator.SizeChanged += (_, _) => DrawNavigator();
        ThemeManager.ThemeChanged += () => { DrawHistory(); DrawNavigator(); }; // the heat gradients are built from theme colours
    }

    // --- data: whole days cached in memory, so scrolling redraws from RAM on every frame ---

    private readonly Dictionary<(DateTime Day, bool Minutely), (HistoryData Data, DateTime LoadedAt)> _dayCache = new();
    private readonly HashSet<(DateTime Day, bool Minutely)> _dayLoading = new();
    private bool _histProcsDirty;
    private const int CachedDays = 10; // ~5 MB a day at per-second resolution

    /// <summary>Re-reads what changed (today) and shows the committed range — buttons, ticks, end of a drag.</summary>
    private Task LoadHistory()
    {
        var to = _historyEnd ?? DateTime.Now;
        var from = to.AddHours(-_historyHours);
        if (_dayCache.TryGetValue((DateTime.Today, _historyHours > 24), out var today) && (DateTime.Now - today.LoadedAt).TotalSeconds > 8 && to >= DateTime.Today)
            RequestDay(DateTime.Today, _historyHours > 24); // today keeps growing; the redraw follows when it lands
        ShowRange(from, to, final: true);
        return RefreshNavigator(from, to);
    }

    /// <summary>
    /// Draws [from, to] from the day cache, synchronously — cheap enough to run on every mouse move. Days
    /// not cached yet are requested in the background and drawn the moment they arrive; the rest is drawn
    /// now. <paramref name="final"/> also refreshes what's too heavy for every frame (the top-apps list).
    /// </summary>
    private void ShowRange(DateTime from, DateTime to, bool final)
    {
        bool minutely = (to - from).TotalHours > 24;
        var data = new HistoryData(new(), new(), new());
        bool complete = true;
        DateTime fromUtc = from.ToUniversalTime(), toUtc = to.ToUniversalTime();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (!_dayCache.TryGetValue((day, minutely), out var cached)) { RequestDay(day, minutely); complete = false; continue; }
            data.System.AddRange(Slice(cached.Data.System, s => s.Time, fromUtc, toUtc));
            data.Processes.AddRange(Slice(cached.Data.Processes, p => p.Time, fromUtc, toUtc));
            data.Events.AddRange(Slice(cached.Data.Events, e => e.Time, fromUtc, toUtc));
        }

        (_historyFrom, _historyTo, _historyMinutely, _hist) = (from, to, minutely, data);
        _histProcsDirty = true;
        HistoryRangeText.Text = $"{from:ddd d MMM HH:mm} – {to:ddd d MMM HH:mm}"
            + (!complete ? " · loading…" : data.System.Count == 0 ? " · nothing recorded" : "")
            + (minutely ? " · per-minute peaks (click to zoom in to every second)" : "");
        DrawHistory();
        if (final || complete) UpdateRangeList(); // skipped mid-drag only while a day is still loading
    }

    /// <summary>The part of a time-sorted list inside [from, to], found by binary search — no scan per frame.</summary>
    private static IEnumerable<T> Slice<T>(List<T> sorted, Func<T, DateTime> time, DateTime from, DateTime to)
    {
        int Lower(DateTime t)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (time(sorted[mid]) < t) lo = mid + 1; else hi = mid; }
            return lo;
        }
        int start = Lower(from), end = Lower(to.AddTicks(1));
        return sorted.GetRange(start, end - start);
    }

    private void RequestDay(DateTime day, bool minutely)
    {
        var key = (day, minutely);
        if (!_dayLoading.Add(key)) return;
        Task.Run(() => HistoryStore.ReadDay(day, minutely)).ContinueWith(t =>
        {
            _dayLoading.Remove(key);
            if (t.IsCompletedSuccessfully) _dayCache[key] = (t.Result, DateTime.Now);
            TrimDayCache();
            if (_historyFrom.Date <= day && day <= _historyTo.Date) ShowRange(_historyFrom, _historyTo, final: !_navDragging);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Keeps the days around the view, drops the ones furthest from it beyond the cap.</summary>
    private void TrimDayCache()
    {
        var center = _historyFrom.AddTicks((_historyTo - _historyFrom).Ticks / 2).Date;
        foreach (var key in _dayCache.Keys.OrderByDescending(k => Math.Abs((k.Day - center).TotalDays)).ToList())
        {
            if (_dayCache.Count <= CachedDays) break;
            if (key.Day >= _historyFrom.Date && key.Day <= _historyTo.Date) continue;
            _dayCache.Remove(key);
        }
    }

    private void UpdateRangeList()
    {
        HistoryRangeTitle.Text = $"TOP APPS IN THIS RANGE (AVERAGE {MetricLabel().ToUpperInvariant()})";
        HistoryRangeList.ItemsSource = HistoryStore.TopProcesses(_hist.Processes, SortKey()).Select(t => new OptionItem
        {
            Name = t.Name,
            Description = $"CPU peak {t.PeakCpu:0}% · {FormatMB(t.PeakRamMB)} peak" + (t.PeakGpu >= 1 ? $" · GPU peak {t.PeakGpu:0}%" : ""),
            Note = _historyMetric switch { "mem" => FormatMB(t.AvgRamMB), "gpu" => $"{t.AvgGpu:0.0}% GPU", _ => $"{t.AvgCpu:0.0}% CPU" },
        }).ToList();
    }

    /// <summary>
    /// The overview strip: the chosen span ending now — or, when the view is further back than that reaches,
    /// the same span centred on the view, so a zoomed-in window never shrinks to a sliver of "the last 3 days".
    /// Up to 6 h it's drawn from the per-second data (an hour of per-minute peaks is only 60 points).
    /// </summary>
    private async Task RefreshNavigator(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        double spanH = _navHours > 0 ? _navHours : Math.Max(1, (now - (HistoryStore.OldestDay() ?? now.Date)).TotalHours);
        spanH = Math.Max(spanH, (to - from).TotalHours * 1.25); // the window must fit, with room to move
        var navTo = now;
        var navFrom = now.AddHours(-spanH);
        // Also once the window is dragged to the strip's left end: re-centring there lets the next drag carry on back.
        if (from < navFrom.AddHours(spanH * 0.1))
        {
            var center = from + (to - from) / 2;
            navTo = center.AddHours(spanH / 2) > now ? now : center.AddHours(spanH / 2);
            navFrom = navTo.AddHours(-spanH);
        }

        bool moved = Math.Abs((navFrom - _navFrom).TotalHours) > spanH * 0.01 || Math.Abs((navTo - _navTo).TotalHours) > spanH * 0.01;
        if (!_historyLoading && (moved || (now - _navLoadedAt).TotalSeconds > 60))
        {
            _historyLoading = true;
            try
            {
                bool perSecond = spanH <= 6;
                _navData = await Task.Run(() => perSecond ? HistoryStore.Read(navFrom, navTo).System : HistoryStore.ReadMinutes(navFrom, navTo, systemOnly: true).System);
                (_navFrom, _navTo, _navLoadedAt) = (navFrom, navTo, DateTime.Now);
            }
            finally { _historyLoading = false; }
        }
        if (!_navDragging) DrawNavigator();
    }

    private static string FormatMB(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    private string MetricLabel() => _historyMetric switch { "mem" => "Memory", "gpu" => "GPU", _ => "CPU" };
    private string SortKey() => _historyMetric switch { "mem" => "ram", "gpu" => "gpu", _ => "cpu" };
    private float Metric(SysSample s) => _historyMetric switch { "mem" => s.Mem, "gpu" => s.Gpu, _ => s.Cpu };
    private float MetricTemp(SysSample s) => _historyMetric == "gpu" ? s.GpuTemp : s.CpuTemp;

    private Color ThemeColor(string key, Color fallback) => (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
    private Color MetricColor() => ThemeColor(_historyMetric switch { "mem" => "AccentMem", "gpu" => "AccentGpu", _ => "AccentCpu" }, Colors.SteelBlue);
    private static readonly Color Hot = Color.FromRgb(0xEF, 0x44, 0x44), Warm = Color.FromRgb(0xF9, 0x73, 0x16), Cool = Color.FromRgb(0x5B, 0x6B, 0xD8);
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
        _selectionRect = null;
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
        // Never more columns than recorded samples: zoomed to a minute, 3px columns would be 0.2 s wide and
        // most would hold nothing, breaking the curve into needles between the 1-second readings.
        double resolution = _historyMinutely ? 60 : 1;
        var cols = MakeColumns(fromUtc, span, w, Math.Max(1, (int)Math.Min(w / 4, span / resolution)));
        double gapLimit = Math.Max(_historyMinutely ? 150 : 5, cols.Seconds * 1.5);

        // Gaps: shaded for sleep, a dashed baseline with a power icon when the PC was off, dashed alone
        // when Sysoptimizer simply wasn't running. Includes the stretch before the first sample.
        var gaps = new List<(DateTime A, DateTime B)>();
        var previous = fromUtc;
        foreach (var s in samples)
        {
            if ((s.Time - previous).TotalSeconds > gapLimit) gaps.Add((previous, s.Time));
            previous = s.Time;
        }
        var end = DateTime.UtcNow < _historyTo.ToUniversalTime() ? DateTime.UtcNow : _historyTo.ToUniversalTime();
        if ((end - previous).TotalSeconds > gapLimit) gaps.Add((previous, end));
        foreach (var (a, b) in gaps) DrawGap(c, X(a), X(b), top, bottom, ClassifyGap(a, b));

        // The metric as each 4px column's peak — a spike survives any zoom level (see SmoothRuns).
        var peaks = Bucket(samples, Metric, cols);
        int bridge = (int)(gapLimit / cols.Seconds);
        BridgeShortGaps(peaks, bridge);
        var accent = MetricColor();
        // Colour follows height: the theme accent at rest, orange from ~70%, red near 100%.
        LinearGradientBrush Heat(byte hotA, byte warmA, byte restA, byte floorA) => new(new GradientStopCollection
        {
            new(Alpha(Hot, hotA), 0), new(Alpha(Warm, warmA), 0.3), new(Alpha(accent, restA), 0.5), new(Alpha(accent, floorA), 1),
        }, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute };
        var areaPath = new Path { Data = SmoothRuns(peaks, cols, Y, baseline: bottom), Fill = Heat(0x66, 0x4A, 0x38, 0x00), IsHitTestVisible = false };
        var linePath = new Path
        {
            Data = SmoothRuns(peaks, cols, Y, baseline: null), Stroke = Heat(0xFF, 0xFF, 0xFF, 0xFF), StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
        };
        linePath.SetResourceReference(UIElement.EffectProperty, _historyMetric switch { "mem" => "LineGlowMem", "gpu" => "LineGlowGpu", _ => "LineGlowCpu" });
        c.Children.Add(areaPath);
        c.Children.Add(linePath);
        (_chartPeaks, _chartWidth, _chartY, _chartCols) = (peaks, w, Y, cols);
        if (_animateChart && samples.Count > 0)
        {
            // A new range or metric eases in; drag frames and live ticks redraw in place, unanimated.
            _animateChart = false;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
            areaPath.BeginAnimation(OpacityProperty, fade);
            linePath.BeginAnimation(OpacityProperty, fade);
        }

        DrawTemperatureBand(c, h - TempBand / 2 - 3, cols, bridge);
        DrawLaunchBadges(c, X, w, bottom);

        // By the range on screen, not the saved setting: mid-drag the view is in the past while it says "follow now".
        if (DateTime.Now >= _historyFrom && DateTime.Now <= _historyTo.AddSeconds(30))
        {
            double nx = Math.Min(w - 1, X(DateTime.UtcNow));
            c.Children.Add(new Line { X1 = nx, X2 = nx, Y1 = 4, Y2 = bottom, Stroke = new SolidColorBrush(Hot), StrokeThickness = 1.5 });
            Place(c, new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Hot) }, nx - 3.5, 1);
        }

        _cursorLine = new Line { Y1 = top - 4, Y2 = bottom, StrokeThickness = 1, Opacity = 0.45, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _cursorLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
        c.Children.Add(_cursorLine);

        // A dot that rides the curve under the cursor, with the value beside it.
        _cursorDot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(accent), StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _cursorDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CardBackground");
        c.Children.Add(_cursorDot);
        _cursorValue = new Border
        {
            Background = new SolidColorBrush(Alpha(accent, 0xE6)), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2),
            Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        c.Children.Add(_cursorValue);
        // A live refresh or a drag frame rebuilds the chart: put the pin (or the resting mouse's cursor) back.
        if (_pinnedUtc is DateTime pin && pin >= fromUtc && pin <= _historyTo.ToUniversalTime()) PlaceCursor(X(pin), pinned: true);
        else if (_pinnedUtc == null && _hoverX is double hx && c.IsMouseOver) PlaceCursor(hx, pinned: false);

        DrawAxis(w);
    }

    /// <summary>
    /// Each recorded run of columns (NaN breaks it) as plateaus — every column flat at its peak — joined by
    /// short rounded steps, AppControl's calm look. A curve through the column centres turned every lone
    /// peak into a needle; a plateau shows it as a tower as wide as the time it covers, and can't overshoot.
    /// With a baseline it's closed down to it, as a fill.
    /// </summary>
    private static StreamGeometry SmoothRuns(float[] values, Columns cols, Func<double, double> y, double? baseline)
    {
        var geometry = new StreamGeometry();
        int n = values.Length;
        double r = Math.Min(cols.Step * 0.35, 4); // half the width of a step's rounded turn
        using (var ctx = geometry.Open())
        {
            int i = 0;
            while (i < n)
            {
                if (float.IsNaN(values[i])) { i++; continue; }
                int j = i;
                while (j < n && !float.IsNaN(values[j])) j++;

                var start = new Point(cols.Left(i), y(values[i]));
                if (baseline is double b)
                {
                    ctx.BeginFigure(new Point(start.X, b), true, true);
                    ctx.LineTo(start, false, true);
                }
                else ctx.BeginFigure(start, false, false);
                for (int k = i; k < j - 1; k++)
                {
                    double edge = cols.Left(k + 1), y0 = y(values[k]), y1 = y(values[k + 1]);
                    ctx.LineTo(new Point(edge - r, y0), true, true);
                    ctx.BezierTo(new Point(edge, y0), new Point(edge, y1), new Point(edge + r, y1), true, true);
                }
                ctx.LineTo(new Point(cols.Left(j), y(values[j - 1])), true, true);
                if (baseline is double b2) ctx.LineTo(new Point(cols.Left(j), b2), false, true);
                i = j;
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Continues the current figure through the points with the same non-overshooting Beziers.</summary>
    private static void SmoothThrough(StreamGeometryContext ctx, List<Point> points)
    {
        for (int k = 1; k < points.Count; k++)
        {
            double mid = (points[k - 1].X + points[k].X) / 2;
            ctx.BezierTo(new Point(mid, points[k - 1].Y), new Point(mid, points[k].Y), points[k], false, true);
        }
    }

    /// <summary>
    /// Joins empty columns no wider than a real gap: zoomed to a minute, a single skipped 1-second reading
    /// (a slow sample makes the next tick wait) would otherwise snap the curve. Longer gaps stay gaps.
    /// </summary>
    private static void BridgeShortGaps(float[] values, int maxGap)
    {
        int i = 0;
        while (i < values.Length)
        {
            if (!float.IsNaN(values[i])) { i++; continue; }
            int j = i;
            while (j < values.Length && float.IsNaN(values[j])) j++;
            if (i > 0 && j < values.Length && j - i <= maxGap)
                for (int k = i; k < j; k++)
                    values[k] = values[i - 1] + (values[j] - values[i - 1]) * (k - i + 1) / (j - i + 1);
            i = j;
        }
    }

    /// <summary>Peak of each time column; NaN where nothing was recorded.</summary>
    private static float[] Bucket(List<SysSample> samples, Func<SysSample, float> value, Columns cols)
    {
        var peaks = new float[cols.Count];
        Array.Fill(peaks, float.NaN);
        foreach (var s in samples)
        {
            int b = (int)Math.Floor((s.Time - cols.Origin).TotalSeconds / cols.Seconds);
            float v = value(s);
            if (b < 0 || b >= cols.Count || float.IsNaN(v)) continue;
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
    private void DrawTemperatureBand(Canvas c, double centerY, Columns cols, int bridge)
    {
        int buckets = cols.Count;
        var temps = Bucket(_hist.System, MetricTemp, cols);
        BridgeShortGaps(temps, bridge);
        if (temps.All(float.IsNaN)) return;
        _chartTemps = temps;
        // Steep on purpose: a desktop CPU works at ~55-90 °C, so that's the whole height — a jump from 68 to
        // 78 °C has to show as a bump, not a hair.
        double Thick(float t) => Math.Clamp((t - 55) / 35 * (TempBand - 6), 2, TempBand - 6);
        // Colour on a fixed scale, so a stretch keeps its colour however the view moves (relative to what's on
        // screen, scrolling a hot stretch away lit up the rest): cool up to 60 °C, coral by 78, red at 90.
        (double At, Color Colour)[] scale = { (0, Cool), (0.6, Color.FromRgb(0xE9, 0x79, 0x5A)), (1, Hot) };
        Color Shade(float t)
        {
            double f = Math.Clamp((t - 60) / 30, 0, 1);
            for (int s = 1; s < scale.Length; s++)
                if (f <= scale[s].At)
                {
                    double g = (f - scale[s - 1].At) / (scale[s].At - scale[s - 1].At);
                    Color a = scale[s - 1].Colour, b = scale[s].Colour;
                    return Color.FromRgb((byte)(a.R + (b.R - a.R) * g), (byte)(a.G + (b.G - a.G) * g), (byte)(a.B + (b.B - a.B) * g));
                }
            return Hot;
        }

        var geometry = new StreamGeometry();
        var stops = new GradientStopCollection();
        // The fill's gradient runs across the band's own bounds (Path brushes map to the geometry's box).
        int firstCol = Array.FindIndex(temps, t => !float.IsNaN(t)), lastCol = Array.FindLastIndex(temps, t => !float.IsNaN(t));
        double bandLeft = cols.Left(firstCol), bandWidth = Math.Max(1, cols.Left(lastCol + 1) - bandLeft);
        using (var g = geometry.Open())
        {
            int i = 0;
            while (i < buckets)
            {
                if (float.IsNaN(temps[i])) { i++; continue; }
                int j = i;
                while (j < buckets && !float.IsNaN(temps[j])) j++;
                // One closed outline per recorded run, smooth along both edges: out along the top, back along the bottom.
                var top = Enumerable.Range(i, j - i).Select(k => new Point(cols.Center(k), centerY - Thick(temps[k]) / 2)).ToList();
                var bottom = Enumerable.Range(i, j - i).Reverse().Select(k => new Point(cols.Center(k), centerY + Thick(temps[k]) / 2)).ToList();
                g.BeginFigure(new Point(cols.Left(i), centerY), true, true);
                g.LineTo(top[0], false, true);
                SmoothThrough(g, top);
                g.LineTo(new Point(cols.Left(j), centerY), false, true);
                g.LineTo(bottom[0], false, true);
                SmoothThrough(g, bottom);
                for (int k = i; k < j; k++) stops.Add(new GradientStop(Shade(temps[k]), (cols.Center(k) - bandLeft) / bandWidth)); // one stop per column: no blur
                i = j;
            }
        }
        var band = new Path { Data = geometry, Fill = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)) };
        band.ToolTip = _historyMetric == "gpu" ? "GPU temperature: thicker and warmer is hotter" : "CPU temperature: thicker and warmer is hotter";
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
        int[] steps = { 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800 };
        int step = steps.FirstOrDefault(s => span / s <= 8, 604800);
        var t = _historyFrom.Date.AddSeconds(Math.Ceiling((_historyFrom - _historyFrom.Date).TotalSeconds / step) * step);
        for (; t <= _historyTo; t = t.AddSeconds(step))
        {
            double x = (t - _historyFrom).TotalSeconds / span * w;
            string text = step >= 86400 || t.TimeOfDay == TimeSpan.Zero ? t.ToString("ddd d") : t.ToString(step < 60 ? "HH:mm:ss" : "HH:mm");
            var label = Label(text);
            label.Width = 60;
            label.TextAlignment = TextAlignment.Center;
            Place(HistoryAxis, label, Math.Clamp(x - 30, 0, w - 60), 0);
        }
    }

    // --- hover / pin / zoom ---

    private DateTime? _pinnedUtc; // a clicked moment: its line, dot and app list stay put while the mouse moves on
    private bool _frozeForPin;    // pinning stopped "follow now"; unpinning resumes it

    private DateTime TimeAt(double x) =>
        _historyFrom.ToUniversalTime().AddSeconds(x / HistoryChart.ActualWidth * (_historyTo - _historyFrom).TotalSeconds);

    private double XOfUtc(DateTime utc) =>
        (utc - _historyFrom.ToUniversalTime()).TotalSeconds / (_historyTo - _historyFrom).TotalSeconds * HistoryChart.ActualWidth;

    private void HistoryChart_MouseMove(object sender, MouseEventArgs e)
    {
        double w = HistoryChart.ActualWidth;
        if (w <= 0 || _historyTo <= _historyFrom) return;
        if (_pan is var (panX, panFrom, panTo))
        {
            if (e.RightButton == MouseButtonState.Released) { EndPan(); return; } // the release went elsewhere
            PanTo(panFrom, panTo, e.GetPosition(HistoryChart).X - panX);
            return;
        }
        double x = Math.Clamp(e.GetPosition(HistoryChart).X, 0, w);
        _hoverX = x;
        if (_pressX != null && e.LeftButton == MouseButtonState.Released) CancelSelection(); // the release went elsewhere
        if (_pressX is double start && e.LeftButton == MouseButtonState.Pressed && (_chartSelecting || Math.Abs(x - start) > DragThreshold))
        {
            _chartSelecting = true;
            HideCursor();
            UpdateSelection(start, x);
            return;
        }
        if (_pinnedUtc != null) return; // pinned: everything stays on the pinned moment
        PlaceCursor(x, pinned: false);
        ShowDetailsAt(TimeAt(x), pinned: false);
    }

    private void HistoryChart_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverX = null;
        if (_pinnedUtc == null) HideCursor();
    }

    // Right button held: the chart is dragged along under the mouse, live, and the range is kept on release.
    private (double X, DateTime From, DateTime To)? _pan;

    private void HistoryChart_MouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_pressX != null || _historyTo <= _historyFrom) return;
        _pan = (e.GetPosition(HistoryChart).X, _historyFrom, _historyTo);
        HideCursor();
        HistoryChart.Cursor = Cursors.SizeWE;
        HistoryChart.CaptureMouse();
        e.Handled = true;
    }

    private void HistoryChart_MouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_pan == null) return;
        e.Handled = true;
        EndPan();
    }

    /// <summary>The range dragged by dx pixels — never past now — redrawn from the day cache.</summary>
    private void PanTo(DateTime from, DateTime to, double dx)
    {
        var shift = TimeSpan.FromSeconds(-dx / HistoryChart.ActualWidth * (to - from).TotalSeconds);
        var now = DateTime.Now;
        if (to + shift > now) shift = now - to;
        ShowRange(from + shift, to + shift, final: false);
    }

    private void EndPan()
    {
        if (_pan == null) return;
        _pan = null; // first: releasing the capture below calls back in here
        HistoryChart.ReleaseMouseCapture();
        HistoryChart.Cursor = Cursors.Cross;
        _historyEnd = _historyTo >= DateTime.Now.AddSeconds(-30) ? null : _historyTo;
        _frozeForPin = false;
        _ = LoadHistory();
    }

    // Press, then: release in place = pin that moment; drag = select a stretch and zoom to it on release.
    private double? _pressX;
    private bool _chartSelecting;
    private Rectangle? _selectionRect;
    private const double DragThreshold = 5;

    private void HistoryChart_MouseDown(object sender, MouseButtonEventArgs e)
    {
        double w = HistoryChart.ActualWidth;
        if (w <= 0 || _historyTo <= _historyFrom) return;
        double x = Math.Clamp(e.GetPosition(HistoryChart).X, 0, w);
        if (e.ClickCount >= 2) { _pressX = null; ZoomTo(TimeAt(x)); return; }
        _pressX = x;
        HistoryChart.CaptureMouse();
    }

    private void HistoryChart_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressX is not double start) return;
        _pressX = null;
        HistoryChart.ReleaseMouseCapture();
        double x = Math.Clamp(e.GetPosition(HistoryChart).X, 0, HistoryChart.ActualWidth);
        if (_chartSelecting) { _chartSelecting = false; ZoomToSelection(Math.Min(start, x), Math.Max(start, x)); return; }
        PinAt(x);
    }

    /// <summary>
    /// Pins a moment: the line, the dot and the app list below stay on it while the mouse moves on, and a
    /// live view stops scrolling so it can't drift away. Clicking elsewhere moves the pin; clicking the pinned
    /// line releases it.
    /// </summary>
    private void PinAt(double x)
    {
        if (_pinnedUtc is DateTime pin && Math.Abs(XOfUtc(pin) - x) <= 6) { Unpin(); return; }
        var time = TimeAt(x);
        _pinnedUtc = time;
        if (_historyEnd == null) { _historyEnd = _historyTo; _frozeForPin = true; }
        PlaceCursor(x, pinned: true);
        ShowDetailsAt(time, pinned: true);
    }

    /// <summary>
    /// Drops a press or selection whose release never arrived (Alt+Tab mid-drag, a window popping up over
    /// this one) — otherwise the next click would finish it and zoom to a zero-width stretch.
    /// </summary>
    private void CancelSelection()
    {
        _pressX = null;
        _chartSelecting = false;
        if (_selectionRect != null) HistoryChart.Children.Remove(_selectionRect);
        _selectionRect = null;
        if (HistoryChart.IsMouseCaptured) HistoryChart.ReleaseMouseCapture();
    }

    /// <summary>Draws the stretch being selected, with its times and length in the title above the app list.</summary>
    private void UpdateSelection(double start, double x)
    {
        double left = Math.Min(start, x), width = Math.Abs(x - start);
        if (_selectionRect == null || !HistoryChart.Children.Contains(_selectionRect))
        {
            var accent = MetricColor();
            _selectionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Alpha(accent, 0x2A)), Stroke = new SolidColorBrush(Alpha(accent, 0xC0)),
                StrokeThickness = 1, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false,
            };
            HistoryChart.Children.Add(_selectionRect);
        }
        _selectionRect.Width = Math.Max(1, width);
        _selectionRect.Height = Math.Max(1, HistoryChart.ActualHeight - BadgeRow - TempBand - 6);
        Canvas.SetLeft(_selectionRect, left);
        Canvas.SetTop(_selectionRect, BadgeRow);
        var (from, to) = (TimeAt(left).ToLocalTime(), TimeAt(left + width).ToLocalTime());
        HistoryCursorTitle.Text = $"Release to zoom to {from:ddd HH:mm:ss} – {to:HH:mm:ss} ({Length(to - from)})";
    }

    private static string Length(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{d.Seconds} s";

    /// <summary>Zooms to exactly the dragged stretch (at least a minute), at per-second resolution under a day.</summary>
    private void ZoomToSelection(double left, double right)
    {
        _animateChart = true;
        ZoomToRange(TimeAt(left).ToLocalTime(), TimeAt(right).ToLocalTime());
    }

    private void ZoomToRange(DateTime from, DateTime to)
    {
        if (to - from < TimeSpan.FromMinutes(1)) to = from.AddMinutes(1);
        _historyHours = (to - from).TotalHours;
        _historyEnd = to >= DateTime.Now.AddSeconds(-30) ? null : to;
        _frozeForPin = false;
        ClearRangePills();
        HistoryCursorTitle.Text = "Hover to see what was running · click to pin a moment · drag across a stretch or scroll to zoom · right-drag to move";
        _ = LoadHistory();
    }

    /// <summary>The wheel zooms in and out around the moment under the mouse, which stays under it.</summary>
    private void HistoryChart_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double x = e.GetPosition(HistoryChart).X, w = HistoryChart.ActualWidth;
        if (w <= 0 || _pressX != null) return;
        double hours = Math.Clamp(_historyHours * Math.Pow(0.8, e.Delta / 120.0), 1.0 / 60, HistoryStore.RetentionDays * 24);
        if (Math.Abs(hours - _historyHours) < 1e-9) return;
        var at = TimeAt(x).ToLocalTime();
        var from = at.AddHours(-hours * x / w);
        ZoomToRange(from, from.AddHours(hours));
    }

    /// <summary>A width no pill offers: the pills no longer describe the window, so none stays lit.</summary>
    private void ClearRangePills()
    {
        foreach (var pill in ((Panel)HistoryRange1h.Parent).Children.OfType<RadioButton>().Where(r => r.GroupName == "HistoryRange"))
            pill.IsChecked = false;
    }

    private void Unpin()
    {
        _pinnedUtc = null;
        if (_hoverX is double hx) { PlaceCursor(hx, pinned: false); ShowDetailsAt(TimeAt(hx), pinned: false); }
        else HideCursor();
        if (_frozeForPin) { _frozeForPin = false; _historyEnd = null; _ = LoadHistory(); }
    }

    /// <summary>The hour around a moment, at per-second resolution; a pin made by the double-click's first click moves along.</summary>
    private void ZoomTo(DateTime utc)
    {
        if (_historyHours <= 1) return;
        _animateChart = true;
        if (_pinnedUtc != null) _pinnedUtc = utc;
        _frozeForPin = false; // zooming is deliberate navigation: unpinning afterwards shouldn't jump back to now
        var end = utc.ToLocalTime().AddMinutes(30);
        _historyEnd = end >= DateTime.Now ? null : end;
        if (HistoryRange1h.IsChecked == true) _ = LoadHistory();
        else HistoryRange1h.IsChecked = true; // its Checked handler reloads
    }

    private void PlaceCursor(double x, bool pinned)
    {
        if (_cursorLine == null) return;
        _cursorLine.X1 = _cursorLine.X2 = x;
        _cursorLine.StrokeDashArray = pinned ? null : new DoubleCollection { 3, 3 };
        _cursorLine.Opacity = pinned ? 0.9 : 0.45;
        _cursorLine.StrokeThickness = pinned ? 1.5 : 1;
        _cursorLine.Visibility = Visibility.Visible;
        MoveCursorDot(x);
    }

    private void HideCursor()
    {
        foreach (var element in new UIElement?[] { _cursorLine, _cursorDot, _cursorValue })
            if (element != null) element.Visibility = Visibility.Collapsed;
    }

    /// <summary>The readings and the running apps at a moment, in the panel under the chart.</summary>
    private void ShowDetailsAt(DateTime time, bool pinned)
    {
        if (_histProcsDirty)
        {
            _histProcs = _hist.Processes.GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.ToList());
            _histProcTimes = _histProcs.Keys.OrderBy(t => t).ToList();
            _histProcsDirty = false;
        }
        double span = (_historyTo - _historyFrom).TotalSeconds;
        double tolerance = Math.Max(_historyMinutely ? 90 : 3, span / Math.Max(1, HistoryChart.ActualWidth) * 3);
        string prefix = pinned ? "PINNED · " : "", suffix = pinned ? "   (click the line to unpin)" : "";

        string Val(float v, string u) => float.IsNaN(v) ? "—" : $"{v:0}{u}";
        if (_hist.System.Count > 0 && Nearest(_hist.System, time, s => s.Time) is var s && Math.Abs((s.Time - time).TotalSeconds) <= tolerance)
            HistoryCursorTitle.Text = $"{prefix}{s.Time.ToLocalTime():ddd HH:mm:ss}{(_historyMinutely ? " (minute peak)" : "")} — CPU {Val(s.Cpu, "%")} · MEM {Val(s.Mem, "%")} · GPU {Val(s.Gpu, "%")} · {Val(s.CpuTemp, "°")} / {Val(s.GpuTemp, "°")}{suffix}";
        else
            HistoryCursorTitle.Text = $"{prefix}{time.ToLocalTime():ddd HH:mm:ss} — nothing recorded{suffix}";

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

    /// <summary>Puts the dot on the curve at the cursor's column, the value in a bubble beside it.</summary>
    private void MoveCursorDot(double x)
    {
        if (_cursorDot == null || _cursorValue == null || _chartPeaks.Length == 0) return;
        int b = Math.Clamp((int)Math.Floor((x - _chartCols.Offset) / _chartCols.Step), 0, _chartPeaks.Length - 1);
        float v = _chartPeaks[b];
        if (float.IsNaN(v)) { _cursorDot.Visibility = _cursorValue.Visibility = Visibility.Collapsed; return; }

        double cx = _chartCols.Center(b), cy = _chartY(v);
        Canvas.SetLeft(_cursorDot, cx - _cursorDot.Width / 2);
        Canvas.SetTop(_cursorDot, cy - _cursorDot.Height / 2);
        float temp = b < _chartTemps.Length ? _chartTemps[b] : float.NaN;
        ((TextBlock)_cursorValue.Child).Text = float.IsNaN(temp) ? $"{v:0}%" : $"{v:0}% · {temp:0}°C";
        _cursorValue.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Beside the dot, flipped to the left near the right edge so it never clips.
        double bx = cx + 10 + _cursorValue.DesiredSize.Width > _chartWidth ? cx - 10 - _cursorValue.DesiredSize.Width : cx + 10;
        Canvas.SetLeft(_cursorValue, bx);
        Canvas.SetTop(_cursorValue, Math.Max(BadgeRow, cy - _cursorValue.DesiredSize.Height - 6));
        _cursorDot.Visibility = _cursorValue.Visibility = Visibility.Visible;
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

        // Day ticks on long spans, hour ticks on short ones — a reference to steer by while dragging.
        bool hourly = span <= 12 * 3600;
        var tickStep = hourly ? TimeSpan.FromHours(span <= 3 * 3600 ? 0.25 : 1) : TimeSpan.FromDays(1);
        var firstTick = hourly ? _navFrom.Date.AddHours(_navFrom.Hour) : _navFrom.Date;
        for (var t = firstTick + tickStep; t < _navTo; t += tickStep)
        {
            if (t <= _navFrom) continue;
            var tick = new Line { X1 = X(t), X2 = X(t), Y1 = 0, Y2 = h, StrokeThickness = 1, Opacity = 0.2 };
            tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
            c.Children.Add(tick);
            if (hourly ? t.Minute == 0 || span <= 3 * 3600 : span / 86400 <= 14)
                Place(c, Label(t.ToString(hourly ? "HH:mm" : "ddd d"), 9), X(t) + 3, 1);
        }

        var navCols = MakeColumns(fromUtc, span, w, Math.Max(1, (int)(w / 3)));
        var peaks = Bucket(_navData, Metric, navCols);
        var navColor = MetricColor();
        c.Children.Add(new Path
        {
            Data = SmoothRuns(peaks, navCols, v => h - 2 - Math.Clamp(v, 0, 100) / 100 * (h - 12), baseline: h),
            Fill = new LinearGradientBrush(Alpha(navColor, 0x70), Alpha(navColor, 0x10), 90), IsHitTestVisible = false,
        });

        var accent = MetricColor();
        double realWidth = X(_historyTo) - X(_historyFrom);
        _navSelection = new Border
        {
            Width = Math.Max(NavGrabWidth, realWidth), Height = h - 2,
            BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Alpha(accent, 0x22)), IsHitTestVisible = false,
        };
        Place(c, _navSelection, Math.Clamp(X(_historyFrom) - (_navSelection.Width - realWidth) / 2, 0, Math.Max(0, w - _navSelection.Width)), 1);

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
    private const double EdgeGrip = 7, MinSelection = 8, NavGrabWidth = 14;

    private NavDrag HitTestSelection(double x)
    {
        if (_navSelection == null) return NavDrag.None;
        double left = Canvas.GetLeft(_navSelection), right = left + _navSelection.Width;
        // A one-hour window over three days is ~9 px wide: full-size grips on both edges would cover all of
        // it, and grabbing its middle would resize instead of move. The grips shrink with the box.
        double grip = Math.Min(EdgeGrip, _navSelection.Width / 4);
        if (Math.Abs(x - left) <= grip) return NavDrag.Left;
        if (Math.Abs(x - right) <= grip) return NavDrag.Right;
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
        UpdateNavigatorLabels();
        PreviewDrag();
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
        PreviewDrag();
    }

    private DateTime _lastPreview;

    /// <summary>The chart follows the box while it's dragged, redrawn from the day cache — about once per frame.</summary>
    private void PreviewDrag()
    {
        if ((DateTime.UtcNow - _lastPreview).TotalMilliseconds < 15) return;
        _lastPreview = DateTime.UtcNow;
        var (from, to) = NavigatorSelectionTimes();
        ShowRange(from, to, final: false);
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
            ClearRangePills();
        }
        _historyEnd = to >= DateTime.Now.AddMinutes(-1) ? null : to;
        _navDrag = NavDrag.None;
        _ = LoadHistory();
    }

    /// <summary>
    /// The times the box stands for. Resizing reads both edges; anything else reads the box's centre and keeps
    /// the view's own length — the box may be drawn wider than the span (NavGrabWidth) so it stays grabbable.
    /// </summary>
    private (DateTime From, DateTime To) NavigatorSelectionTimes()
    {
        double w = HistoryNavigator.ActualWidth, span = (_navTo - _navFrom).TotalSeconds;
        double left = Canvas.GetLeft(_navSelection!), right = left + _navSelection!.Width;
        if (_navDrag is NavDrag.Left or NavDrag.Right)
            return (_navFrom.AddSeconds(left / w * span), _navFrom.AddSeconds(right / w * span));
        var center = _navFrom.AddSeconds((left + right) / 2 / w * span);
        var half = TimeSpan.FromHours(_historyHours / 2);
        return (center - half, center + half);
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
        _animateChart = true;
        if (sender is RadioButton { Tag: string hours }) _historyHours = double.Parse(hours);
        if (IsLoaded) _ = LoadHistory();
    }

    private void HistoryMetric_Checked(object sender, RoutedEventArgs e)
    {
        _animateChart = true;
        if (sender is RadioButton { Tag: string metric }) _historyMetric = metric;
        if (IsLoaded) _ = LoadHistory();
    }

    private void HistoryPrev_Click(object sender, RoutedEventArgs e)
    {
        _animateChart = true;
        _historyEnd = (_historyEnd ?? DateTime.Now).AddHours(-_historyHours);
        _ = LoadHistory();
    }

    private void HistoryNext_Click(object sender, RoutedEventArgs e)
    {
        _animateChart = true;
        var next = (_historyEnd ?? DateTime.Now).AddHours(_historyHours);
        _historyEnd = next >= DateTime.Now ? null : next;
        _ = LoadHistory();
    }

    private void HistoryNow_Click(object sender, RoutedEventArgs e)
    {
        _animateChart = true;
        _historyEnd = null;
        _ = LoadHistory();
    }

    private void NavigatorSpan_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NavigatorSpanCombo.SelectedItem is not ComboBoxItem { Tag: string hours }) return;
        _navHours = double.Parse(hours);
        _navLoadedAt = DateTime.MinValue;
        if (!IsLoaded) return;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer"))
            key.SetValue("NavigatorHours", (int)_navHours, RegistryValueKind.DWord);
        _ = LoadHistory();
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
