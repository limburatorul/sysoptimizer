using System.Globalization;
using System.Net;
using System.Text;

namespace Sysoptimizer.Services;

/// <summary>One PC's week, for the weekly report: its minute summaries, and its boots when they could be read.</summary>
public sealed record ReportPart(string Name, HistoryData? Week, List<Boot>? Boots, string? Error);

/// <summary>The weekly report: a self-contained HTML page, one section per PC.</summary>
public static class ReportService
{
    public static string Html(DateTime from, DateTime to, IEnumerable<ReportPart> parts)
    {
        var html = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        html.Append($$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Sysoptimizer — week of {{from:d MMM}}</title>
            <style>
              body { font: 14px/1.5 "Segoe UI", sans-serif; background: #12161c; color: #e6e9ee; margin: 0 auto; max-width: 900px; padding: 24px 16px; }
              h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 18px; margin: 32px 0 8px; border-top: 1px solid #2a3240; padding-top: 20px; }
              h3 { font-size: 13px; text-transform: uppercase; letter-spacing: .05em; color: #8b96a8; margin: 18px 0 6px; }
              .muted { color: #8b96a8; } .bad { color: #ef6b6b; }
              table { border-collapse: collapse; width: 100%; } td, th { padding: 4px 8px; text-align: left; border-bottom: 1px solid #222a35; }
              th { color: #8b96a8; font-weight: 600; } td.n { text-align: right; font-variant-numeric: tabular-nums; }
            </style></head><body>
            <h1>Your PCs this week</h1>
            <div class="muted">{{from:dddd d MMMM}} – {{to:dddd d MMMM yyyy}} · made by Sysoptimizer</div>
            """);
        foreach (var part in parts)
        {
            html.Append($"<h2>{E(part.Name)}</h2>");
            if (part.Error != null) { html.Append($"<p class=\"bad\">Couldn't read this PC — {E(part.Error)}</p>"); continue; }
            var week = part.Week!;
            if (week.System.Count == 0) { html.Append("<p class=\"muted\">Nothing recorded this week.</p>"); }
            else
            {
                html.Append($"<p class=\"muted\">Recorded {week.System.Count / 60.0:0} hours. \"Typical\" is the middle minute, \"peak\" the busiest second.</p>");
                html.Append("<table><tr><th></th><th>Typical</th><th>Peak</th></tr>");
                void Row(string label, Func<SysSample, float> pick, string unit)
                {
                    var values = week.System.Select(pick).Where(v => !float.IsNaN(v)).OrderBy(v => v).ToList();
                    if (values.Count > 0) html.Append($"<tr><td>{label}</td><td class=\"n\">{values[values.Count / 2]:0}{unit}</td><td class=\"n\">{values[^1]:0}{unit}</td></tr>");
                }
                Row("CPU", s => s.Cpu, "%");
                Row("Memory", s => s.Mem, "%");
                Row("GPU", s => s.Gpu, "%");
                Row("CPU temperature", s => s.CpuTemp, " °C");
                Row("GPU temperature", s => s.GpuTemp, " °C");
                Row("Power (CPU + GPU)", s => float.IsNaN(s.CpuWatts) ? s.GpuWatts : float.IsNaN(s.GpuWatts) ? s.CpuWatts : s.CpuWatts + s.GpuWatts, " W");
                var battery = week.System.Select(s => s.BatteryPct).Where(v => !float.IsNaN(v)).ToList();
                if (battery.Count > 0) html.Append($"<tr><td>Battery, lowest</td><td></td><td class=\"n\">{battery.Min():0}%</td></tr>");
                html.Append("</table>");

                void Apps(string title, string sortBy, Func<ProcessTotal, string> value)
                {
                    var top = HistoryStore.TopProcesses(week.Processes, sortBy, 5);
                    if (top.Count == 0) return;
                    html.Append($"<h3>{title}</h3><table>");
                    foreach (var t in top) html.Append($"<tr><td>{E(t.Name)}</td><td class=\"n\">{value(t)}</td></tr>");
                    html.Append("</table>");
                }
                Apps("Busiest apps (CPU)", "cpu", t => $"{t.AvgCpu:0.0}% average · {t.PeakCpu:0}% peak");
                Apps("Biggest apps (memory)", "ram", t => $"{t.AvgRamMB / 1024:0.0} GB average · {t.PeakRamMB / 1024:0.0} GB peak");
            }

            var alerts = week.Events.Where(e => e.Kind == "alert").ToList();
            html.Append($"<h3>Alerts</h3>");
            if (alerts.Count == 0) html.Append("<p class=\"muted\">None.</p>");
            else
            {
                html.Append("<table>");
                foreach (var g in alerts.GroupBy(a => a.Detail).OrderByDescending(g => g.Count()).Take(10))
                    html.Append($"<tr><td>{E(g.Key)}</td><td class=\"n\">{(g.Count() > 1 ? $"× {g.Count()}" : g.First().Time.ToLocalTime().ToString("ddd HH:mm"))}</td></tr>");
                html.Append("</table>");
            }

            if (part.Boots is { Count: > 0 } boots)
            {
                html.Append($"<h3>Windows starts</h3><p>{boots.Count} start{(boots.Count == 1 ? "" : "s")}, {boots.Average(b => b.Seconds):0} s on average"
                    + $" (slowest {boots.Max(b => b.Seconds):0} s, {boots.MaxBy(b => b.Seconds)!.StartUtc.ToLocalTime():ddd HH:mm}).</p>");
                var slow = boots.SelectMany(b => b.SlowedBy).GroupBy(s => s.Name).Select(g => (Name: g.Key, Extra: g.Sum(s => s.ExtraSeconds), Times: g.Count()))
                    .OrderByDescending(s => s.Extra).Take(5).ToList();
                if (slow.Count > 0)
                {
                    html.Append("<table><tr><th>Held the start up</th><th>Extra time</th></tr>");
                    foreach (var s in slow) html.Append($"<tr><td>{E(s.Name)}</td><td class=\"n\">{s.Extra:0.#} s{(s.Times > 1 ? $" over {s.Times} starts" : "")}</td></tr>");
                    html.Append("</table>");
                }
            }
        }
        html.Append("</body></html>");
        return html.ToString();
    }
}
