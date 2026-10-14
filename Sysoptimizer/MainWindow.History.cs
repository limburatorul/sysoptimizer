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

    // --- electricity ---

    private const string EnergyKey = @"Software\Sysoptimizer\Energy";
    private double _kwhPrice = double.NaN, _extraWatts;
    private string _currency = "";
    private bool _energyLoading;

    private void LoadEnergySettings()
    {
        _energyLoading = true;
        using (var key = Registry.CurrentUser.OpenSubKey(EnergyKey))
        {
            EnergyPriceBox.Text = key?.GetValue("Price") as string ?? "";
            EnergyCurrencyBox.Text = key?.GetValue("Currency") as string ?? "";
            EnergyExtraBox.Text = (key?.GetValue("ExtraWatts") as int? ?? 0).ToString();
        }
        _energyLoading = false;
        ReadEnergySettings(save: false);
    }

    private void EnergySetting_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_energyLoading && IsLoaded) { ReadEnergySettings(save: true); UpdateEnergyText(); }
    }

    /// <summary>Price with a decimal comma or point, whatever Windows' own format (0,80 or 0.80); a box that doesn't parse is marked and ignored.</summary>
    private void ReadEnergySettings(bool save)
    {
        bool priceOk = double.TryParse(EnergyPriceBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double price) && price is >= 0 and < 1000;
        bool extraOk = int.TryParse(EnergyExtraBox.Text.Trim(), out int extra) && extra is >= 0 and <= 2000;
        foreach (var (box, ok) in new[] { (EnergyPriceBox, priceOk || EnergyPriceBox.Text.Trim().Length == 0), (EnergyExtraBox, extraOk) })
        {
            if (ok) box.ClearValue(Control.BorderBrushProperty); else box.SetResourceReference(Control.BorderBrushProperty, "StatusBad");
        }
        _kwhPrice = priceOk ? price : double.NaN;
        if (extraOk) _extraWatts = extra;
        _currency = EnergyCurrencyBox.Text.Trim();
        if (!save) return;
        using var key = Registry.CurrentUser.CreateSubKey(EnergyKey);
        key.SetValue("Price", EnergyPriceBox.Text.Trim());
        key.SetValue("Currency", _currency);
        if (extraOk) key.SetValue("ExtraWatts", extra, RegistryValueKind.DWord);
    }

    /// <summary>
    /// The energy the range on screen used: CPU + GPU watts, plus the rest of the PC as set, over the time it
    /// was recorded (gaps — off or asleep — count nothing). From minute peaks on long ranges, so a little high.
    /// </summary>
    private (double Kwh, double Hours) RangeEnergy()
    {
        double joules = 0, seconds = 0;
        var system = _hist.System;
        for (int i = 1; i < system.Count; i++)
        {
            double dt = (system[i].Time - system[i - 1].Time).TotalSeconds;
            if (dt <= 0 || dt > (_historyMinutely ? 150 : 5)) continue; // a gap: off, asleep, or not recording
            float w = Raw(system[i], "power");
            joules += ((float.IsNaN(w) ? 0 : w) + _extraWatts) * dt;
            seconds += dt;
        }
        return (joules / 3.6e6, seconds / 3600);
    }

    private string Money(double amount) => $"{amount:0.00} {_currency}".Trim();

    private void UpdateEnergyText()
    {
        EnergyPanel.Visibility = _historyMetric == "power" && _historyApp == null ? Visibility.Visible : Visibility.Collapsed;
        if (EnergyPanel.Visibility != Visibility.Visible) return;
        var (kwh, hours) = RangeEnergy();
        if (hours <= 0) { EnergyText.Text = "Nothing recorded in this range."; return; }
        string used = $"{kwh:0.##} kWh in the {hours:0.#} h recorded here" + (_historyMinutely ? " (from minute peaks, a little high)" : "");
        if (double.IsNaN(_kwhPrice)) { EnergyText.Text = used + " — type a price to see the cost."; return; }
        double cost = kwh * _kwhPrice;
        // The pace of the range, kept up for 30 days: what a month like this costs.
        double month = cost / Math.Max(1, (_historyTo - _historyFrom).TotalHours) * 24 * 30;
        EnergyText.Text = $"{used}: {Money(cost)} · about {Money(month)} a month at this pace";
    }

    private void InitHistory()
    {
        LoadEnergySettings();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            HistoryStore.RetentionDays = key?.GetValue("RetentionDays") is int days ? days : 90;
        RetentionCombo.SelectedItem = RetentionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == HistoryStore.RetentionDays.ToString()) ?? RetentionCombo.Items[1];

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            if (key?.GetValue("NavigatorHours") is int savedSpan) _navHours = savedSpan;
        NavigatorSpanCombo.SelectedItem = NavigatorSpanCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _navHours.ToString()) ?? NavigatorSpanCombo.SelectedItem;

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            if (key?.GetValue("HistoryListHeight") is int listHeight) HistoryListsRow.Height = new GridLength(Math.Max(70, listHeight));

        HistoryChart.SizeChanged += (_, _) => DrawHistory();
        HistoryChart.LostMouseCapture += (_, _) => { if (_pressX != null) CancelSelection(); EndPan(); }; // capture taken away mid-press
        HistoryNavigator.SizeChanged += (_, _) => DrawNavigator();
        HistoryChart.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && _dayCache.Count == 0) _ = LoadHistory(); }; // back from the tray after its days were let go
        ThemeManager.ThemeChanged += () => { DrawHistory(); DrawNavigator(); }; // the heat gradients are built from theme colours
    }

    // --- data: whole days cached in memory, so scrolling redraws from RAM on every frame ---

    private readonly Dictionary<(DateTime Day, bool Minutely), (HistoryData Data, DateTime LoadedAt)> _dayCache = new();
    private readonly HashSet<(DateTime Day, bool Minutely)> _dayLoading = new();
    private bool _histProcsDirty;
    private bool _histWatts; // the range has CPU or GPU draw recorded, so the Power and Battery lists can rank apps by watts
    // A per-second day is 10-20 MB parsed, a minute summary under 1: few of the first, a month of the second.
    private const int CachedRawDays = 4, CachedMinuteDays = 35;

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
        // A drag redraws ~60 times a second: its frames refill the same lists instead of copying a day's
        // hundred thousand rows into new ones each time — that garbage forced a full collection several
        // times a second, freezing the window for a second or more. The final view gets lists of its own,
        // which the background ranking can read while the next drag reuses these.
        var data = final ? new HistoryData(new(), new(), new()) : _dragData;
        var before = final ? new List<SysSample>() : _dragBefore;
        if (!final) { data.System.Clear(); data.Processes.Clear(); data.Events.Clear(); before.Clear(); }
        bool complete = true;
        DateTime fromUtc = from.ToUniversalTime(), toUtc = to.ToUniversalTime();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (!_dayCache.TryGetValue((day, minutely), out var cached)) { RequestDay(day, minutely); complete = false; continue; }
            AddSlice(data.System, cached.Data.System, s => s.Time, fromUtc, toUtc);
            AddSlice(data.Processes, cached.Data.Processes, p => p.Time, fromUtc, toUtc);
            AddSlice(data.Events, cached.Data.Events, e => e.Time, fromUtc, toUtc);
        }

        if (_compare)
            for (var day = (from - (to - from)).Date; day <= from.Date; day = day.AddDays(1))
            {
                if (!_dayCache.TryGetValue((day, minutely), out var cached)) { RequestDay(day, minutely); complete = false; continue; }
                AddSlice(before, cached.Data.System, s => s.Time, (from - (to - from)).ToUniversalTime(), fromUtc.AddTicks(-1));
            }

        (_historyFrom, _historyTo, _historyMinutely, _hist, _before) = (from, to, minutely, data, before);
        _histProcsDirty = true;
        _histWatts = data.System.Exists(s => s.CpuWatts > 0 || s.GpuWatts > 0);
        HistoryRangeText.Text = (_source != null ? $"{_source.Name} · " : "") + $"{from:ddd d MMM HH:mm} – {to:ddd d MMM HH:mm}"
            + (_sourceError != null ? $" · {_source?.Name ?? "history"}: {_sourceError}" : !complete ? " · loading…" : data.System.Count == 0 ? " · nothing recorded" : "")
            + (minutely ? " · per-minute peaks (click to zoom in to every second)" : "");
        DrawHistory();
        if (final) UpdateRangeList(); // mid-drag the list waits for the release
    }

    private readonly HistoryData _dragData = new(new(), new(), new());
    private readonly List<SysSample> _dragBefore = new();

    /// <summary>Adds the part of a time-sorted list inside [from, to], found by binary search — no scan, no copy in between.</summary>
    private static void AddSlice<T>(List<T> into, List<T> sorted, Func<T, DateTime> time, DateTime from, DateTime to)
    {
        int start = Lower(sorted, time, from), end = Lower(sorted, time, to.AddTicks(1));
        into.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sorted).Slice(start, end - start));
    }

    /// <summary>The index of the first item at or after <paramref name="t"/> in a time-sorted list.</summary>
    private static int Lower<T>(List<T> sorted, Func<T, DateTime> time, DateTime t)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (time(sorted[mid]) < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // Work that can wait for a quiet moment: queued once at Background priority, below input and rendering,
    // so a mouse sweep or a drag never waits for it — it runs when the UI has time, with the latest state.
    private readonly HashSet<string> _queued = new();

    private void Later(string key, Action action)
    {
        if (!_queued.Add(key)) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { _queued.Remove(key); action(); });
    }

    private void RequestDay(DateTime day, bool minutely)
    {
        var key = (day, minutely);
        if (!_dayLoading.Add(key)) return;
        var source = _source;
        Task.Run(() => source?.ReadDay(day, minutely) ?? HistoryStore.ReadDay(day, minutely)).ContinueWith(t =>
        {
            if (source != _source) return; // the view switched PCs while this was on its way
            _dayLoading.Remove(key);
            if (t.IsCompletedSuccessfully) { _dayCache[key] = (t.Result, DateTime.Now); _sourceError = null; }
            else _sourceError = t.Exception?.InnerException?.Message; // shown under the chart; the next refresh retries
            TrimDayCache();
            if (ViewFrom.Date <= day && day <= _historyTo.Date) ShowRange(_historyFrom, _historyTo, final: !_navDragging);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Keeps the days around the view, drops the ones furthest from it beyond the cap.</summary>
    private void TrimDayCache()
    {
        var center = _historyFrom.AddTicks((_historyTo - _historyFrom).Ticks / 2).Date;
        foreach (bool minutely in new[] { false, true })
        {
            int keep = minutely ? CachedMinuteDays : CachedRawDays;
            foreach (var key in _dayCache.Keys.Where(k => k.Minutely == minutely).OrderByDescending(k => Math.Abs((k.Day - center).TotalDays)).ToList())
            {
                if (_dayCache.Keys.Count(k => k.Minutely == minutely) <= keep) break;
                if (key.Day >= ViewFrom.Date && key.Day <= _historyTo.Date) continue;
                _dayCache.Remove(key);
            }
        }
    }

    private DateTime? _historyHiddenSince;

    /// <summary>
    /// Once History has been out of sight for two minutes (another tab, or the window in the tray), lets go of
    /// the days it kept — up to a few hundred MB for a week of another PC — and gives the memory back to
    /// Windows. Opening History again reads them anew, from disk or from the watched PC. Called every second.
    /// </summary>
    private void ReleaseHistoryIfHidden()
    {
        if (HistoryChart.IsVisible || _dayCache.Count == 0) { _historyHiddenSince = null; return; }
        _historyHiddenSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _historyHiddenSince.Value).TotalMinutes < 2) return;
        _historyHiddenSince = null;
        _dayCache.Clear();
        (_hist, _before, _navData, _navLoadedAt) = (new(new(), new(), new()), new(), new(), default);
        (_histProcs, _histProcTimes, _histProcsDirty) = (new(), new(), true);
        foreach (var list in new System.Collections.IList[] { _dragData.System, _dragData.Processes, _dragData.Events, _dragBefore }) list.Clear();
        _dragData.System.TrimExcess(); _dragData.Processes.TrimExcess(); _dragData.Events.TrimExcess(); _dragBefore.TrimExcess();
        HistoryCursorList.ItemsSource = HistoryRangeList.ItemsSource = null;
        _detailsShown = null;
        HistoryStore.ForgetToday();
        foreach (var remote in _remotes) remote.Forget();
        // The freed days sit in the large-object heap, which isn't compacted on its own: once, now that they're gone.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void UpdateRangeList()
    {
        int ticket = ++_rangeTicket; // a ranking still running for an older call mustn't land after this one
        bool energyMetric = _historyMetric is "power" or "battery";
        HistoryMeasuredCheck.Visibility = energyMetric ? Visibility.Visible : Visibility.Collapsed;
        if (energyMetric && HistoryMeasuredCheck.IsChecked == true)
        {
            // Asked once the range settles, not on every frame of a drag.
            if (_measuredTimer == null)
            {
                _measuredTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _measuredTimer.Tick += (_, _) => { _measuredTimer.Stop(); _ = ShowMeasured(); };
            }
            _measuredTimer.Stop();
            _measuredTimer.Start();
            return;
        }
        HistoryRangeTitle.Text = $"TOP APPS IN THIS RANGE (AVERAGE {MetricLabel().ToUpperInvariant()})";
        UpdateEnergyText();
        // A day holds a hundred thousand app rows: ranked off the UI thread, so a refresh never stalls it.
        var hist = _hist;
        string metric = _historyMetric, sortKey = SortKey();
        double price = _kwhPrice, hours = ListMetric == "watts" && !double.IsNaN(price) ? RangeEnergy().Hours : 0;
        Func<List<OptionItem>> rank = ListMetric switch
        {
            // Averaged over the moments each app drew, not the whole range: a game played for ten minutes
            // of a day still reads its own frame rate. The low is the worst moment recorded.
            "fps" => () => hist.Processes.Where(p => p.Fps >= 1).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Avg: g.Average(p => p.Fps), Low: g.Min(p => p.Fps), Minutes: g.Count()))
                .OrderByDescending(t => t.Minutes).Take(10)
                .Select(t => new OptionItem { Name = t.Name, Description = $"lowest {t.Low:0} FPS", Note = $"{t.Avg:0} FPS" }).ToList(),
            "watts" => () =>
            {
                int snapshots = Math.Max(1, hist.Processes.Select(p => p.Time).Distinct().Count());
                return hist.Processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Name: g.Key, Watts: g.Sum(p => WattsOf(p, hist.System)) / snapshots, PeakCpu: g.Max(p => p.Cpu), PeakGpu: g.Max(p => p.Gpu)))
                    .Where(t => t.Watts > 0).OrderByDescending(t => t.Watts).Take(10)
                    .Select(t => new OptionItem
                    {
                        Name = t.Name,
                        Description = $"CPU peak {t.PeakCpu:0}%" + (t.PeakGpu >= 1 ? $" · GPU peak {t.PeakGpu:0}%" : ""),
                        // An app's share of the bill: its average watts over the hours recorded here.
                        Note = $"{t.Watts:0.0} W" + (double.IsNaN(price) ? "" : $" · {Money(t.Watts * hours / 1000 * price)}"),
                    }).ToList();
            },
            _ => () => HistoryStore.TopProcesses(hist.Processes, sortKey).Select(t => new OptionItem
            {
                Name = t.Name,
                Description = $"CPU peak {t.PeakCpu:0}% · {FormatMB(t.PeakRamMB)} peak" + (t.PeakGpu >= 1 ? $" · GPU peak {t.PeakGpu:0}%" : ""),
                Note = metric switch { "mem" => FormatMB(t.AvgRamMB), "gpu" => $"{t.AvgGpu:0.0}% GPU", _ => $"{t.AvgCpu:0.0}% CPU" },
            }).ToList(),
        };
        Task.Run(rank).ContinueWith(t =>
        {
            if (ticket == _rangeTicket) HistoryRangeList.ItemsSource = t.Result; // else the range moved on: a newer ranking follows
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private int _rangeTicket;
    private System.Windows.Threading.DispatcherTimer? _measuredTimer;
    private int _measuredRequest;

    private void HistoryMeasured_Click(object sender, RoutedEventArgs e) => UpdateRangeList();

    /// <summary>The range's apps by the energy Windows itself measured (SRUM) — this PC's, or the watched one's.</summary>
    private async Task ShowMeasured()
    {
        int request = ++_measuredRequest;
        var source = _source;
        DateTime from = _historyFrom.ToUniversalTime(), to = _historyTo.ToUniversalTime();
        HistoryRangeTitle.Text = "TOP APPS IN THIS RANGE (MEASURED BY WINDOWS) · LOADING…";
        string text;
        try { text = await Task.Run(() => source == null ? EnergyLines(from, to) : source.Energy(from, to)); }
        catch (RemoteHistoryException ex) { text = "!" + ex.Message; }
        if (request != _measuredRequest || source != _source) return; // the range or the PC changed meanwhile

        HistoryRangeTitle.Text = "TOP APPS IN THIS RANGE (MEASURED BY WINDOWS)";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Where(l => !l.StartsWith('!')).Select(l => l.Split('\t'))
            .Where(f => f.Length == 2 && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(f => new OptionItem
            {
                Name = f[0],
                Description = "CPU, GPU, screen, disk and network, as Windows measured them",
                Note = $"{double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture):0.##} Wh",
            }).ToList();
        if (rows.Count == 0)
            rows.Add(new OptionItem
            {
                Name = lines.FirstOrDefault(l => l.StartsWith('!')) is { } error ? $"Couldn't read Windows' figures — {error[1..]}" : "Nothing measured in this range",
                Description = "Windows writes its figures about once an hour and keeps about a week of them.",
            });
        HistoryRangeList.ItemsSource = rows;
    }

    /// <summary>
    /// The overview strip: the chosen span ending now — or, when the view is further back than that reaches,
    /// the same span centred on the view, so a zoomed-in window never shrinks to a sliver of "the last 3 days".
    /// Up to 6 h it's drawn from the per-second data (an hour of per-minute peaks is only 60 points).
    /// </summary>
    private async Task RefreshNavigator(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        var source = _source;
        DateTime? oldest = null;
        if (_navHours <= 0)
            try { oldest = await Task.Run(() => source?.OldestDay() ?? HistoryStore.OldestDay()); }
            catch (RemoteHistoryException) { } // the chart's own caption says the PC can't be reached
        if (source != _source) return;
        double spanH = _navHours > 0 ? _navHours : Math.Max(1, (now - (oldest ?? now.Date)).TotalHours);
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
                var data = await Task.Run(() => source != null
                    ? (perSecond ? source.Read(navFrom, navTo) : source.ReadMinutes(navFrom, navTo)).System
                    : perSecond ? HistoryStore.Read(navFrom, navTo).System : HistoryStore.ReadMinutes(navFrom, navTo, systemOnly: true).System);
                if (source != _source) return;
                (_navData, _navFrom, _navTo, _navLoadedAt) = (data, navFrom, navTo, DateTime.Now);
            }
            catch (RemoteHistoryException) { } // shown in the chart's caption; the strip keeps what it had
            finally { _historyLoading = false; }
        }
        if (!_navDragging) DrawNavigator();
    }

    private static string FormatMB(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    // The app lists rank by CPU on the Disk and Network views: per-app disk and network use isn't recorded.
    // On Power and Battery they rank by estimated watts, when the sensors gave the CPU or GPU draw.
    private bool SystemOnlyMetric => _historyMetric is "disk" or "net" or "power" or "fan" or "battery";
    private string ListMetric => _historyMetric is "power" or "battery" && _histWatts ? "watts" : SystemOnlyMetric ? "cpu" : _historyMetric;
    private string MetricLabel() => ListMetric switch { "mem" => "Memory", "gpu" => "GPU", "watts" => "estimated power", "fps" => "FPS while drawing", _ => "CPU" };

    /// <summary>
    /// An app's estimated draw: the CPU's watts split by each app's share of the CPU use, the same for the GPU —
    /// the way Task Manager's "Power usage" guesses it. Screen, Wi-Fi and the rest of the board aren't counted.
    /// </summary>
    private float WattsOf(ProcSample p) => WattsOf(p, _hist.System);

    private static float WattsOf(ProcSample p, List<SysSample> system)
    {
        var s = Nearest(system, p.Time, x => x.Time);
        static float Part(float app, float total, float watts) =>
            app > 0 && watts > 0 && total > 0 ? Math.Min(1, app / total) * watts : 0;
        return Part(p.Cpu, s.Cpu, s.CpuWatts) + Part(p.Gpu, s.Gpu, s.GpuWatts);
    }
    private string SortKey() => ListMetric switch { "mem" => "ram", "gpu" => "gpu", _ => "cpu" };

    private string? _historyApp; // null = the whole PC; otherwise the chart plots this one app
    private bool _compare;       // also draw the stretch just before the range, as long as it, dashed
    private List<SysSample> _before = new();
    private DateTime ViewFrom => _compare ? _historyFrom - (_historyTo - _historyFrom) : _historyFrom; // the days the view needs
    private double _scale = 100;  // what the chart's full height stands for, in the metric's own unit

    /// <summary>Percent metrics fill a fixed 0-100 %; disk, network and an app's memory scale to what's on screen.</summary>
    private bool InPercent => _historyApp == null ? IsPercentMetric(_historyMetric) : _historyMetric is "cpu" or "gpu";
    private static bool IsPercentMetric(string metric) => metric is "cpu" or "mem" or "gpu" or "battery";
    private static float Raw(SysSample s, string metric) => RawOf(metric)(s);

    /// <summary>The reading a metric plots, picked once — a chart reads it for every one of a day's 86,400 samples.</summary>
    private static Func<SysSample, float> RawOf(string metric) => metric switch
    {
        "mem" => s => s.Mem, "gpu" => s => s.Gpu, "disk" => s => s.DiskMBs, "net" => s => s.NetMbps, "fan" => s => s.FanRpm,
        "battery" => s => s.BatteryPct, "fps" => s => s.Fps,
        "power" => s => float.IsNaN(s.CpuWatts) ? s.GpuWatts : float.IsNaN(s.GpuWatts) ? s.CpuWatts : s.CpuWatts + s.GpuWatts,
        _ => s => s.Cpu,
    };

    /// <summary>The plotted value, 0-100 of the chart's height.</summary>
    private Func<SysSample, float> PlotOf()
    {
        var raw = RawOf(_historyMetric);
        float k = (float)(100 / _scale);
        return s => raw(s) * k;
    }
    private string FormatValue(double v) => InPercent
        ? (v < 10 ? $"{v:0.0}%" : $"{v:0}%")
        : _historyMetric switch { "disk" => $"{v:0.#} MB/s", "net" => $"{v:0.#} Mbps", "power" => $"{v:0} W", "fan" => $"{v:0} RPM", "fps" => $"{v:0} FPS", _ => FormatMB(v) };

    /// <summary>1, 2 or 5 × a power of ten, at least the value: a scale whose labels read cleanly.</summary>
    private static double NiceCeiling(double max)
    {
        if (!(max > 0)) return 1;
        double step = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var k in new[] { 1.0, 2, 5 }) if (k * step >= max) return k * step;
        return 10 * step;
    }

    /// <summary>
    /// One app's line, from the process snapshots (every 10 s, every 2 s in a spike): its CPU, RAM and GPU
    /// in the Cpu/Mem/Gpu slots. Only the busiest apps are recorded in each snapshot, so a snapshot without
    /// it counts as 0 — it was too idle to make the list.
    /// </summary>
    private List<SysSample> AppSeries(string app)
    {
        EnsureProcessIndex();
        return _histProcTimes.Select(t =>
        {
            var p = _histProcs[t].Find(x => string.Equals(x.Name, app, StringComparison.OrdinalIgnoreCase));
            // Frames: no line where the app drew none (or wasn't recorded), rather than a drop to 0.
            return p.Name == null ? new SysSample(t, 0, 0, 0, float.NaN, float.NaN) : new SysSample(t, p.Cpu, p.RamMB, p.Gpu, float.NaN, float.NaN, Fps: p.Fps);
        }).ToList();
    }
    private float MetricTemp(SysSample s) => _historyMetric is "gpu" or "fps" ? s.GpuTemp : s.CpuTemp;

    private Color ThemeColor(string key, Color fallback) => (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
    private Color MetricColor() => ThemeColor(_historyMetric switch { "mem" or "net" => "AccentMem", "gpu" or "fps" => "AccentGpu", _ => "AccentCpu" }, Colors.SteelBlue);
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

        var samples = _historyApp == null ? _hist.System : AppSeries(_historyApp);
        var shift = _historyTo - _historyFrom;
        var before = _compare && _historyApp == null ? _before.Select(s => s with { Time = s.Time + shift }).ToList() : null;
        _scale = InPercent ? 100 : NiceCeiling(samples.Concat(before ?? new()).Select(x => Raw(x, _historyMetric)).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max());
        UpdateCompareText();
        if (_historyMetric == "fps")
        {
            _scale = Math.Max(30, _scale); // an idle scale of "1 FPS" says nothing
            if (!samples.Any(x => Raw(x, "fps") >= 1))
            {
                var none = Label(_historyApp == null ? "No frames recorded here. FPS is recorded for the app in front while it draws — a game you play."
                                                     : $"{_historyApp} drew no frames that were recorded here.", 12);
                none.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Place(c, none, (w - none.DesiredSize.Width) / 2, (top + bottom) / 2 - 10);
            }
        }
        foreach (var (v, text) in new[] { (100.0, FormatValue(_scale)), (50.0, FormatValue(_scale / 2)), (0.0, InPercent ? "0%" : "0") })
        {
            // Only the middle line, dashed: the top of the plot is 100% (or the scale's top) and its floor is 0.
            if (v == 50)
            {
                var grid = new Line { X1 = 0, X2 = w, Y1 = Y(v), Y2 = Y(v), StrokeThickness = 1, Opacity = 0.18, StrokeDashArray = new DoubleCollection { 2, 4 } };
                grid.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
                c.Children.Add(grid);
            }
            var label = Label(text, 9);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(c, label, w - label.DesiredSize.Width - 6, Y(v) - (v == 100 ? -1 : 13));
        }

        // Never more columns than recorded samples: zoomed to a minute, 3px columns would be 0.2 s wide and
        // most would hold nothing, breaking the curve into needles between the 1-second readings.
        double resolution = _historyMinutely ? 60 : 1;
        var cols = MakeColumns(fromUtc, span, w, Math.Max(1, (int)Math.Min(w / 4, span / resolution)));
        double gapLimit = Math.Max(_historyMinutely ? 150 : 5, cols.Seconds * 1.5);

        // Gaps: shaded for sleep, a dashed baseline with a power icon when the PC was off, dashed alone
        // when Sysoptimizer simply wasn't running. Includes the stretch before the first sample.
        var gaps = new List<(DateTime A, DateTime B)>();
        var previous = fromUtc;
        foreach (var s in _hist.System) // the PC's own record says when it was off or asleep, in app view too
        {
            if ((s.Time - previous).TotalSeconds > gapLimit) gaps.Add((previous, s.Time));
            previous = s.Time;
        }
        var end = DateTime.UtcNow < _historyTo.ToUniversalTime() ? DateTime.UtcNow : _historyTo.ToUniversalTime();
        if ((end - previous).TotalSeconds > gapLimit) gaps.Add((previous, end));
        foreach (var (a, b) in gaps) DrawGap(c, X(a), X(b), top, bottom, ClassifyGap(a, b));

        // The metric as each 4px column's peak — a spike survives any zoom level (see SmoothRuns).
        var metric = PlotOf();
        var peaks = Bucket(samples, metric, cols);
        // An app's line comes from the snapshots, 10 s apart: bridge across them, not just a skipped second.
        int bridge = (int)((_historyApp != null && !_historyMinutely ? Math.Max(25, gapLimit) : gapLimit) / cols.Seconds);
        BridgeShortGaps(peaks, bridge);
        var accent = MetricColor();
        // Colour follows height: the theme accent at rest, its warning colour from ~70%, its bad colour near 100%.
        Color hot = ThemeColor("StatusBad", Hot), warm = ThemeColor("StatusWarn", Warm);
        // The top of an auto-scaled chart is just "the most in view", and a full battery is good news: no alarm colours.
        if (!InPercent || _historyMetric == "battery") hot = warm = accent;
        LinearGradientBrush Heat(byte hotA, byte warmA, byte restA, byte floorA) => new(new GradientStopCollection
        {
            new(Alpha(hot, hotA), 0), new(Alpha(warm, warmA), 0.3), new(Alpha(accent, restA), 0.5), new(Alpha(accent, floorA), 1),
        }, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute };
        var areaPath = new Path { Data = SmoothRuns(peaks, cols, Y, baseline: bottom), Fill = Heat(0x66, 0x4A, 0x38, 0x00), IsHitTestVisible = false };
        var linePath = new Path
        {
            Data = SmoothRuns(peaks, cols, Y, baseline: null), Stroke = Heat(0xFF, 0xFF, 0xFF, 0xFF), StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
        };
        linePath.SetResourceReference(UIElement.EffectProperty, _historyMetric switch { "mem" or "net" => "LineGlowMem", "gpu" or "fps" => "LineGlowGpu", _ => "LineGlowCpu" });
        c.Children.Add(areaPath);
        if (before != null)
        {
            // The stretch before, laid over this one: a quiet dashed line under the real one.
            var beforePeaks = Bucket(before, metric, cols);
            BridgeShortGaps(beforePeaks, bridge);
            var beforePath = new Path
            {
                Data = SmoothRuns(beforePeaks, cols, Y, baseline: null), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 },
                Opacity = 0.75, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
            };
            beforePath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
            c.Children.Add(beforePath);
        }
        c.Children.Add(linePath);
        (_chartPeaks, _chartWidth, _chartY, _chartCols) = (peaks, w, Y, cols);
        // The newest reading on screen gets a glowing dot, like the live graphs: where the line ends, it's "now".
        int lastCol = Array.FindLastIndex(peaks, v => !float.IsNaN(v));
        if (lastCol >= 0)
        {
            var endDot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(accent), IsHitTestVisible = false };
            endDot.SetResourceReference(UIElement.EffectProperty, _historyMetric switch { "mem" or "net" => "LineGlowMem", "gpu" or "fps" => "LineGlowGpu", _ => "LineGlowCpu" });
            Place(c, endDot, Math.Min(cols.Center(lastCol), w - 5) - 4, Y(peaks[lastCol]) - 4);
        }
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
        DrawChanges(c, X, w, bottom);

        // By the range on screen, not the saved setting: mid-drag the view is in the past while it says "follow now".
        if (DateTime.Now >= _historyFrom && DateTime.Now <= _historyTo.AddSeconds(30))
        {
            // A quiet marker in the text colour, starting under the badge row: red is kept for heat, and the dot
            // stays clear of the card's rounded corner.
            double nx = Math.Min(w - 1, X(DateTime.UtcNow));
            var nowLine = new Line { X1 = nx, X2 = nx, Y1 = BadgeRow - 6, Y2 = bottom, StrokeThickness = 1, Opacity = 0.6, IsHitTestVisible = false };
            nowLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
            c.Children.Add(nowLine);
            var nowDot = Place(c, new Ellipse { Width = 7, Height = 7, IsHitTestVisible = false }, Math.Min(nx - 3.5, w - 8), BadgeRow - 9.5);
            nowDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextPrimary");
        }

        _cursorLine = new Line { Y1 = top - 4, Y2 = bottom, StrokeThickness = 1, Opacity = 0.45, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _cursorLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
        c.Children.Add(_cursorLine);

        // A dot that rides the curve under the cursor, with a card beside it: the value and the apps behind it.
        _cursorDot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(accent), StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _cursorDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Surface"); // CardBackground is transparent in some themes
        c.Children.Add(_cursorDot);
        _cursorValue = new Border
        {
            Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1), Width = 250,
            Child = new StackPanel(), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _cursorValue.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        _cursorValue.SetResourceReference(Border.BorderBrushProperty, "ButtonBorder");
        _cursorValue.SetResourceReference(Border.CornerRadiusProperty, "PopupRadius");
        _cardColumn = -1;
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
        long origin = cols.Origin.Ticks;
        double ticksPerColumn = cols.Seconds * TimeSpan.TicksPerSecond;
        foreach (ref readonly var s in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples))
        {
            long offset = s.Time.Ticks - origin;
            if (offset < 0) continue;
            int b = (int)(offset / ticksPerColumn);
            if (b >= cols.Count) continue;
            float v = value(s);
            if (float.IsNaN(v)) continue;
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
        var temps = Bucket(_hist.System, _historyMetric is "gpu" or "fps" ? s => s.GpuTemp : s => s.CpuTemp, cols);
        BridgeShortGaps(temps, bridge);
        if (temps.All(float.IsNaN)) return;
        _chartTemps = temps;
        // Thickness in proportion to the temperature itself (100 °C = full height): a scale starting at 55 °C
        // drew 73 °C ten times thicker than 42 °C, a difference that looked huge for one that isn't.
        // The colour is what marks the hot stretches.
        double Thick(float t) => Math.Clamp(t / 100 * (TempBand - 6), 3, TempBand - 6);
        // Colour on a fixed scale, so a stretch keeps its colour however the view moves (relative to what's on
        // screen, scrolling a hot stretch away lit up the rest): the theme's cool up to 60 °C, its warm by 78, its
        // bad colour at 90. Alpha blends too, so a translucent cool lets normal temperatures recede.
        (double At, Color Colour)[] scale =
        {
            (0, ThemeColor("TempCool", Cool)), (0.6, ThemeColor("TempWarm", Color.FromRgb(0xE9, 0x79, 0x5A))), (1, ThemeColor("StatusBad", Hot)),
        };
        Color Shade(float t)
        {
            double f = Math.Clamp((t - 60) / 30, 0, 1);
            for (int s = 1; s < scale.Length; s++)
                if (f <= scale[s].At)
                {
                    double g = (f - scale[s - 1].At) / (scale[s].At - scale[s - 1].At);
                    Color a = scale[s - 1].Colour, b = scale[s].Colour;
                    return Color.FromArgb((byte)(a.A + (b.A - a.A) * g), (byte)(a.R + (b.R - a.R) * g), (byte)(a.G + (b.G - a.G) * g), (byte)(a.B + (b.B - a.B) * g));
                }
            return scale[^1].Colour;
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
            var count = new TextBlock { Text = g.Count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
            var badge = new Border
            {
                Background = new SolidColorBrush(accent), Padding = new Thickness(5, 1, 5, 1), MinWidth = 20, Child = count,
                ToolTip =string.Join("\n", g.Take(20).Select(e => $"{e.Time.ToLocalTime():HH:mm:ss}  {e.Detail}")) + (g.Count > 20 ? $"\n… and {g.Count - 20} more" : ""),
            };
            badge.SetResourceReference(Border.CornerRadiusProperty, "PillRadius"); // a capsule, or a hard tag in the square themes
            AutomationPropertiesName(badge, $"{g.Count} apps launched");
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(c, badge, gx - badge.DesiredSize.Width / 2, 2);
        }
    }

    /// <summary>The kinds of change the History chart marks, with their icon and how the list names them.</summary>
    internal static readonly Dictionary<string, (string Glyph, string Label)> ChangeKinds = new()
    {
        ["installed"] = ("\uE896", "Installed"), ["removed"] = ("\uE74D", "Removed"), ["updated"] = ("\uE777", "Updated"),
        ["update"] = ("\uE777", "Windows update"), ["driver"] = ("\uE772", "Driver"),
    };

    /// <summary>
    /// Changes to the PC — apps installed, removed or updated, Windows updates, drivers — as small tags at the
    /// foot of the chart with a dashed line up through the curve: so "it's been slower since Tuesday" meets
    /// what was installed on Tuesday. Hover a tag for the list; close ones share a tag.
    /// </summary>
    private void DrawChanges(Canvas c, Func<DateTime, double> x, double w, double bottom)
    {
        var groups = new List<List<HistoryEvent>>();
        foreach (var e in _hist.Events.Where(e => ChangeKinds.ContainsKey(e.Kind)).OrderBy(e => e.Time))
        {
            if (groups.Count == 0 || x(e.Time) - x(groups[^1][0].Time) > 30) groups.Add(new());
            groups[^1].Add(e);
        }
        foreach (var g in groups)
        {
            double gx = Math.Clamp(x(g[0].Time), 14, w - 14);
            var line = new Line { X1 = gx, X2 = gx, Y1 = BadgeRow, Y2 = bottom - 20, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = 0.5, IsHitTestVisible = false };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
            c.Children.Add(line);
            var icons = new TextBlock { Text = string.Concat(g.Select(e => ChangeKinds[e.Kind].Glyph).Distinct().Take(3)), FontFamily = new FontFamily(IconFont), FontSize = 11 };
            icons.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icons);
            if (g.Count > 1) content.Children.Add(new TextBlock { Text = $" {g.Count}", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            var tag = new Border
            {
                Padding = new Thickness(5, 2, 5, 2), BorderThickness = new Thickness(1), Child = content,
                ToolTip = string.Join("\n", g.Take(20).Select(e => $"{e.Time.ToLocalTime():ddd HH:mm}  {ChangeKinds[e.Kind].Label}: {e.Detail}")) + (g.Count > 20 ? $"\n… and {g.Count - 20} more" : ""),
            };
            tag.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
            tag.SetResourceReference(Border.BorderBrushProperty, "ButtonBorder");
            tag.SetResourceReference(Border.CornerRadiusProperty, "PillRadius");
            AutomationPropertiesName(tag, g.Count == 1 ? $"{ChangeKinds[g[0].Kind].Label}: {g[0].Detail}" : $"{g.Count} changes to the PC");
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(c, tag, gx - tag.DesiredSize.Width / 2, bottom - tag.DesiredSize.Height - 2);
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
        Later("details", () => { if (_pinnedUtc == null && _hoverX is double hx) ShowDetailsAt(TimeAt(hx), pinned: false); });
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
        if (_pan is not var (startX, _, _)) return;
        e.Handled = true;
        if (Math.Abs(e.GetPosition(HistoryChart).X - startX) > DragThreshold) { EndPan(); return; }
        // A right-click without a drag: let go of everything chosen — the app, the pinned moment.
        _pan = null;
        HistoryChart.ReleaseMouseCapture();
        HistoryChart.Cursor = Cursors.Cross;
        if (_historyApp != null) HistoryAppClear_Click(this, new RoutedEventArgs());
        if (_pinnedUtc != null) Unpin();
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
        HistoryCursorTitle.Text = "Hover to see what was running · click to pin a moment · drag across a stretch or scroll to zoom · click an app below to chart it · right-drag to move, right-click to clear";
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
    private object? _detailsShown;
    private int _cardColumn = -1; // the chart column the cursor card was filled for; -1 after a redraw // what the panel under the chart shows now, to skip rebuilding the same

    private void ShowDetailsAt(DateTime time, bool pinned)
    {
        double span = (_historyTo - _historyFrom).TotalSeconds;
        double tolerance = Math.Max(_historyMinutely ? 90 : 3, span / Math.Max(1, HistoryChart.ActualWidth) * 3);
        string prefix = pinned ? "PINNED · " : "", suffix = pinned ? "   (click the line to unpin)" : "";

        string Val(float v, string u) => float.IsNaN(v) ? "—" : $"{v:0}{u}";
        if (_hist.System.Count > 0 && Nearest(_hist.System, time, s => s.Time) is var s && Math.Abs((s.Time - time).TotalSeconds) <= tolerance)
            HistoryCursorTitle.Text = $"{prefix}{s.Time.ToLocalTime():ddd HH:mm:ss}{(_historyMinutely ? " (minute peak)" : "")} — CPU {Val(s.Cpu, "%")} · MEM {Val(s.Mem, "%")} · GPU {Val(s.Gpu, "%")} · {Val(s.CpuTemp, "°")} / {Val(s.GpuTemp, "°")}{suffix}";
        else
            HistoryCursorTitle.Text = $"{prefix}{time.ToLocalTime():ddd HH:mm:ss} — nothing recorded{suffix}";

        var apps = ProcessesAt(time, tolerance);
        // Apps are recorded every 10 s: most mouse moves land on the same snapshot as the last one.
        var shown = (_hist, apps is { Count: > 0 } ? apps[0].Time : default(DateTime?), ListMetric);
        if (Equals(shown, _detailsShown)) return;
        _detailsShown = shown;
        HistoryCursorList.ItemsSource = apps?.OrderByDescending(MetricOf)
            .Select(p => new OptionItem
            {
                Name = p.Name,
                Description = FormatMB(p.RamMB) + (p.Gpu >= 1 ? $" · GPU {p.Gpu:0}%" : ""),
                Note = ListMetric switch { "watts" => $"{WattsOf(p):0.0} W", "fps" when p.Fps >= 1 => $"{p.Fps:0} FPS", _ => $"{p.Cpu:0.0}% CPU" },
            }).ToList();
    }

    private float MetricOf(ProcSample p) => ListMetric switch { "mem" => p.RamMB, "gpu" => p.Gpu, "watts" => WattsOf(p), "fps" => float.IsNaN(p.Fps) ? 0 : p.Fps, _ => p.Cpu };

    private void EnsureProcessIndex()
    {
        if (!_histProcsDirty) return;
        _histProcs = _hist.Processes.GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.ToList());
        _histProcTimes = _histProcs.Keys.OrderBy(t => t).ToList();
        _histProcsDirty = false;
    }

    /// <summary>
    /// The apps recorded nearest a moment (every 10 s, every 2 s in a spike), or null if none is close. A binary
    /// search in the range's time-sorted rows: no index to rebuild each time the range refreshes.
    /// </summary>
    private List<ProcSample>? ProcessesAt(DateTime time, double tolerance)
    {
        var rows = _hist.Processes;
        if (rows.Count == 0) return null;
        int i = Lower(rows, p => p.Time, time);
        var at = i == rows.Count || (i > 0 && time - rows[i - 1].Time < rows[i].Time - time) ? rows[i - 1].Time : rows[i].Time;
        if (Math.Abs((at - time).TotalSeconds) > Math.Max(60, tolerance)) return null;
        int start = Lower(rows, p => p.Time, at);
        return rows.GetRange(start, Lower(rows, p => p.Time, at.AddTicks(1)) - start);
    }

    /// <summary>
    /// Puts the dot on the curve at the cursor's column and the card beside it, AppControl-style: the moment,
    /// the temperature and the value, then the five apps using the most of this metric then, and the rest.
    /// </summary>
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
        // A column is a few pixels wide and the card says the same across it: rebuild it only on a new one.
        if (_cardColumn != b) { _cardColumn = b; FillCursorCard(TimeAt(x), v, temp); }
        // Beside the cursor, flipped to the left near the right edge. Its height isn't known until layout, so it
        // hangs from the top when the curve is high and stands on the bottom (above the temperature band) when
        // it's low — either way it stays inside the chart without measuring it.
        double bx = x + 14 + _cursorValue.Width > _chartWidth ? x - 14 - _cursorValue.Width : x + 14;
        Canvas.SetLeft(_cursorValue, Math.Max(4, bx));
        double h = HistoryChart.ActualHeight;
        if (cy > h / 2)
        {
            Canvas.SetTop(_cursorValue, double.NaN);
            Canvas.SetBottom(_cursorValue, h - Math.Min(h - TempBand - 8, cy + 20));
        }
        else
        {
            Canvas.SetBottom(_cursorValue, double.NaN);
            Canvas.SetTop(_cursorValue, Math.Max(BadgeRow, cy - 20));
        }
        _cursorDot.Visibility = _cursorValue.Visibility = Visibility.Visible;
    }

    private void FillCursorCard(DateTime time, float value, float temp)
    {
        var rows = (StackPanel)_cursorValue!.Child;
        rows.Children.Clear();
        bool mem = ListMetric == "mem";
        string Amount(float v) => mem ? FormatMB(v) : ListMetric == "watts" ? $"{v:0.0} W" : ListMetric == "fps" ? $"{v:0} FPS" : v < 10 ? $"{v:0.0}%" : $"{v:0}%";
        double raw = value * _scale / 100;

        rows.Children.Add(CardRow(time.ToLocalTime().ToString(_historyMinutely ? "ddd d MMM HH:mm" : "ddd d MMM HH:mm:ss"),
            (float.IsNaN(temp) ? "" : $"{temp:0}°C   ") + FormatValue(raw), secondary: true, bold: true, bottom: 6));

        double span = (_historyTo - _historyFrom).TotalSeconds;
        var apps = ProcessesAt(time, Math.Max(_historyMinutely ? 90 : 3, span / Math.Max(1, HistoryChart.ActualWidth) * 3));
        if (apps == null) { rows.Children.Add(CardRow("No apps recorded near this moment", "", secondary: true)); return; }
        if (_historyApp != null)
        {
            var p = apps.Find(x => string.Equals(x.Name, _historyApp, StringComparison.OrdinalIgnoreCase));
            if (p.Name == null) { rows.Children.Add(CardRow($"{_historyApp} was idle or not running", "", secondary: true)); return; }
            rows.Children.Add(CardRow("CPU", $"{p.Cpu:0.0}%"));
            rows.Children.Add(CardRow("Memory", FormatMB(p.RamMB)));
            rows.Children.Add(CardRow("GPU", $"{p.Gpu:0.0}%"));
            return;
        }
        if (SystemOnlyMetric)
        {
            var at = _hist.System.Count > 0 ? Nearest(_hist.System, time, x => x.Time) : default;
            if (_historyMetric == "power" && !float.IsNaN(at.CpuWatts + at.GpuWatts))
                rows.Children.Add(CardRow($"CPU {at.CpuWatts:0} W · GPU {at.GpuWatts:0} W", "", secondary: true, bottom: 2));
            rows.Children.Add(CardRow(ListMetric == "watts" ? "Estimated per app, from CPU and GPU use:" : "Not recorded per app — the top CPU users then:", "", secondary: true, bottom: 2));
        }
        var ranked = apps.Where(p => MetricOf(p) > 0).OrderByDescending(MetricOf).ToList();
        foreach (var p in ranked.Take(5)) rows.Children.Add(CardRow(p.Name, Amount(MetricOf(p))));
        // CPU and GPU shares add up to the total, so the rest is what the top five leave out; memory per app doesn't.
        int more = Math.Max(0, ranked.Count - 5);
        float rest = value - ranked.Take(5).Sum(MetricOf);
        if (!mem && _historyMetric is "cpu" or "gpu" && rest >= 1) rows.Children.Add(CardRow(more > 0 ? $"{more} more recorded and everything else" : "Everything else", $"{rest:0}%", secondary: true, top: 6));
        else if (more > 0) rows.Children.Add(CardRow($"{more} more recorded, listed below", "", secondary: true, top: 6));
    }

    private static Grid CardRow(string label, string value, bool secondary = false, bool bold = false, double top = 0, double bottom = 0)
    {
        var row = new Grid { Margin = new Thickness(0, top + 2, 0, bottom + 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var weight = bold ? FontWeights.SemiBold : FontWeights.Normal;
        var name = new TextBlock { Text = label, FontSize = 11, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        var amount = new TextBlock { Text = value, FontSize = 11, FontWeight = weight, Margin = new Thickness(10, 0, 0, 0) };
        System.Windows.Documents.Typography.SetNumeralAlignment(amount, FontNumeralAlignment.Tabular);
        name.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondary" : "TextPrimary");
        amount.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondary" : "TextPrimary");
        Grid.SetColumn(amount, 1);
        row.Children.Add(name);
        row.Children.Add(amount);
        return row;
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
        string navMetric = _historyMetric;
        double navScale = !IsPercentMetric(navMetric)
            ? NiceCeiling(_navData.Select(x => Raw(x, navMetric)).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max()) : 100;
        var peaks = Bucket(_navData, x => (float)(Raw(x, navMetric) / navScale * 100), navCols);
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
            BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Alpha(accent, 0x22)), IsHitTestVisible = false,
        };
        _navSelection.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
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

    private void HistoryCompare_Click(object sender, RoutedEventArgs e)
    {
        _compare = HistoryCompareToggle.IsChecked == true;
        _animateChart = true;
        if (IsLoaded) _ = LoadHistory();
    }

    /// <summary>The range against the one just before it: average and peak of the metric, and the change.</summary>
    private void UpdateCompareText()
    {
        CompareText.Visibility = _compare ? Visibility.Visible : Visibility.Collapsed;
        if (!_compare) return;
        if (_historyApp != null) { CompareText.Text = "Compare works on the whole PC — go back to it to see the dashed line."; return; }
        (double Avg, double Peak)? Stats(List<SysSample> list)
        {
            var values = list.Select(s => (double)Raw(s, _historyMetric)).Where(v => !double.IsNaN(v)).ToList();
            return values.Count == 0 ? null : (values.Average(), values.Max());
        }
        var span = _historyTo - _historyFrom;
        string before = span.TotalHours < 48 ? $"the {span.TotalHours:0.#} h before" : $"the {span.TotalDays:0.#} days before";
        string Change(double now, double then) => InPercent
            ? (Math.Abs(now - then) < 0.05 ? "the same" : $"{now - then:+0.#;-0.#} point{(Math.Abs(Math.Round(now - then, 1)) == 1 ? "" : "s")}")
            : then <= 0 ? (now <= 0 ? "the same" : "up from nothing") : Math.Abs(now / then - 1) < 0.005 ? "the same" : $"{(now / then - 1) * 100:+0;-0}%";
        CompareText.Text = (Stats(_hist.System), Stats(_before)) switch
        {
            (null, _) => "Nothing recorded in this range to compare.",
            (_, null) => $"Nothing recorded in {before} to compare with.",
            ({ } n, { } b) => $"{MetricWord()} averaged {FormatValue(n.Avg)}, peak {FormatValue(n.Peak)} — {before} (dashed): {FormatValue(b.Avg)}, peak {FormatValue(b.Peak)}."
                              + $" Average {Change(n.Avg, b.Avg)}, peak {Change(n.Peak, b.Peak)}." + (_historyMinutely ? " From minute peaks." : ""),
        };
    }

    private string MetricWord() => _historyMetric switch
    {
        "mem" => "Memory", "gpu" => "GPU", "fps" => "FPS", "disk" => "Disk", "net" => "Network", "power" => "Power", "fan" => "The fans", "battery" => "Battery", _ => "CPU",
    };

    private void HistoryMetric_Checked(object sender, RoutedEventArgs e)
    {
        _animateChart = true;
        if (sender is RadioButton { Tag: string metric }) _historyMetric = metric;
        if (IsLoaded) _ = LoadHistory();
    }

    /// <summary>A row in the lists under the chart was clicked: chart that one app (its CPU, memory or GPU).</summary>
    private void HistoryApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptionItem { Name: { } app } } || app.Length == 0) return;
        _historyApp = app;
        if (SystemOnlyMetric) HistoryMetricCpu.IsChecked = true; // per app, only CPU, memory and GPU were recorded
        HistoryMetricDisk.IsEnabled = HistoryMetricNet.IsEnabled = HistoryMetricPower.IsEnabled = HistoryMetricFan.IsEnabled = HistoryMetricBattery.IsEnabled = false;
        HistoryAppChip.Content = $"{app}  ✕";
        HistoryAppChip.Visibility = Visibility.Visible;
        _animateChart = true;
        DrawHistory();
    }

    private void HistoryAppClear_Click(object sender, RoutedEventArgs e)
    {
        _historyApp = null;
        HistoryMetricDisk.IsEnabled = HistoryMetricNet.IsEnabled = HistoryMetricPower.IsEnabled = HistoryMetricFan.IsEnabled = HistoryMetricBattery.IsEnabled = true;
        HistoryAppChip.Visibility = Visibility.Collapsed;
        _animateChart = true;
        DrawHistory();
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

    private void HistorySplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer");
        key.SetValue("HistoryListHeight", (int)HistoryListsRow.ActualHeight, RegistryValueKind.DWord);
    }

    /// <summary>
    /// The range on screen, as two CSV files: the readings (per second, or per-minute peaks on long ranges)
    /// and the apps recorded with them. Written in this PC's list separator and number format, so Excel opens
    /// them straight into columns whatever the regional settings.
    /// </summary>
    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        string pc = _source?.Name ?? Environment.MachineName;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (Excel)|*.csv",
            FileName = $"Sysoptimizer {pc} {_historyFrom:yyyy-MM-dd HHmm} to {_historyTo:yyyy-MM-dd HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string sep = culture.TextInfo.ListSeparator;
        string N(float v) => float.IsNaN(v) ? "" : v.ToString("0.##", culture);
        string Csv(string text) => text.Contains(sep) || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
        string When(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var system = new System.Text.StringBuilder();
        system.AppendLine(string.Join(sep, "Time", "CPU %", "Memory %", "GPU %", "CPU °C", "GPU °C", "Disk MB/s", "Network Mbps", "CPU W", "GPU W", "Fan RPM", "Battery %", "FPS"));
        foreach (var x in _hist.System)
            system.AppendLine(string.Join(sep, When(x.Time), N(x.Cpu), N(x.Mem), N(x.Gpu), N(x.CpuTemp), N(x.GpuTemp), N(x.DiskMBs), N(x.NetMbps), N(x.CpuWatts), N(x.GpuWatts), N(x.FanRpm), N(x.BatteryPct), N(x.Fps)));
        var apps = new System.Text.StringBuilder();
        apps.AppendLine(string.Join(sep, "Time", "App", "CPU %", "Memory MB", "GPU %", "FPS"));
        foreach (var p in _hist.Processes)
            apps.AppendLine(string.Join(sep, When(p.Time), Csv(p.Name), N(p.Cpu), N(p.RamMB), N(p.Gpu), N(p.Fps)));

        string appsPath = System.IO.Path.ChangeExtension(dialog.FileName, null) + " - apps.csv";
        try
        {
            // UTF-8 with a BOM: without it Excel reads "°C" as mojibake.
            System.IO.File.WriteAllText(dialog.FileName, system.ToString(), new System.Text.UTF8Encoding(true));
            System.IO.File.WriteAllText(appsPath, apps.ToString(), new System.Text.UTF8Encoding(true));
            Log($"Exported {_hist.System.Count} readings and {_hist.Processes.Count} app rows to {dialog.FileName} (+ \"- apps.csv\").");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log($"Export failed — {ex.Message}");
        }
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
