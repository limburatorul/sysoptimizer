namespace Sysoptimizer.Services;

public sealed record SlowStarter(string Name, double ExtraSeconds);

/// <param name="Seconds">From power-on to a usable desktop with startup apps settled (Windows' "boot time").</param>
/// <param name="DesktopSeconds">To the desktop alone (Windows' "main path").</param>
public sealed record Boot(DateTime StartUtc, double Seconds, double DesktopSeconds, int StartupApps, List<SlowStarter> SlowedBy);

/// <summary>
/// What a week of history says that a glance at the graphs doesn't: memory that kept running out, an app
/// that holds gigabytes or keeps the CPU busy all week, a CPU that ran very hot, Windows starting slower than
/// the week before. Plain sentences, the most telling first; nothing when there's nothing to say.
/// </summary>
public static class InsightService
{
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase) { "Idle", "System", "Memory Compression", "Registry", "Secure System" };

    /// <param name="week">The last seven days at one row per minute (S: the minute's peaks; P: its top apps).</param>
    /// <param name="boots">Recent Windows starts, any order: the last seven days are compared with the seven before.</param>
    public static List<string> From(HistoryData week, List<Boot> boots, DateTime nowUtc)
    {
        var said = new List<string>();
        int minutes = week.System.Count;
        double days = Math.Max(1, (nowUtc - (week.System.Count > 0 ? week.System[0].Time : nowUtc)).TotalDays);
        string Hours(double mins) => mins >= 90 ? $"{mins / 60:0.#} h" : $"{mins:0} min";
        string Size(double mb) => mb >= 1024 ? $"{mb / 1024:0.#} GB" : $"{mb:0} MB";
        var apps = week.Processes.Where(p => !NotApps.Contains(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (minutes >= 60)
        {
            // Memory that kept running short, and who held it then.
            var full = week.System.Where(s => s.Mem >= 85).Select(s => s.Time).ToHashSet();
            if (full.Count / days >= 60)
            {
                var holders = week.Processes.Where(p => full.Contains(p.Time) && !NotApps.Contains(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Average(p => p.RamMB)).Take(3).Select(g => g.Key).ToList();
                said.Add($"Memory was over 85% for about {Hours(full.Count / days)} a day" + (holders.Count > 0 ? $", mostly held by {string.Join(", ", holders)}" : "")
                         + ". Closing some of them, or more RAM, would stop Windows swapping to disk.");
            }

            // An app that holds gigabytes whenever it runs.
            foreach (var g in apps.Where(g => g.Count() >= 60).Select(g => (Name: g.Key, Avg: g.Average(p => p.RamMB), Peak: g.Max(p => p.RamMB), Minutes: g.Count()))
                         .Where(a => a.Avg >= 3072).OrderByDescending(a => a.Avg).Take(2))
                said.Add($"{g.Name} held {Size(g.Avg)} on average while it ran ({Hours(g.Minutes)} this week), up to {Size(g.Peak)}.");

            // An app that keeps the CPU busy all week, not just while it's used.
            foreach (var g in apps.Where(g => g.Count() >= minutes / 2).Select(g => (Name: g.Key, Cpu: g.Sum(p => p.Cpu) / minutes, Share: (double)g.Count() / minutes))
                         .Where(a => a.Cpu >= 5).OrderByDescending(a => a.Cpu).Take(2))
                said.Add($"{g.Name} kept about {g.Cpu:0}% of the CPU busy, running {g.Share * 100:0}% of the time — worth a look if it's not something you use all day.");

            // Very hot, and what was busy then.
            var hot = week.System.Where(s => s.CpuTemp >= 90).Select(s => s.Time).ToHashSet();
            if (hot.Count >= 30)
            {
                string? busiest = week.Processes.Where(p => hot.Contains(p.Time) && !NotApps.Contains(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Sum(p => p.Cpu)).Select(g => g.Key).FirstOrDefault();
                said.Add($"The CPU ran at 90 °C or more for {Hours(hot.Count)} this week" + (busiest != null ? $", mostly while {busiest} was busy" : "") + ".");
            }
        }

        // Windows starting slower than the week before, and what's new among what holds it up.
        var recent = boots.Where(b => b.StartUtc > nowUtc.AddDays(-7)).ToList();
        var before = boots.Where(b => b.StartUtc <= nowUtc.AddDays(-7) && b.StartUtc > nowUtc.AddDays(-14)).ToList();
        if (recent.Count >= 2 && before.Count >= 2)
        {
            double now = recent.Average(b => b.Seconds), then = before.Average(b => b.Seconds);
            if (now - then >= 10 && now >= then * 1.2)
            {
                var known = before.SelectMany(b => b.SlowedBy).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = recent.SelectMany(b => b.SlowedBy).Where(s => !known.Contains(s.Name)).GroupBy(s => s.Name)
                    .OrderByDescending(g => g.Sum(s => s.ExtraSeconds)).Take(2).Select(g => g.Key).ToList();
                said.Add($"Windows takes {now:0} s to start, {now - then:0} s longer than the week before" + (added.Count > 0 ? $" — new among what holds it up: {string.Join(", ", added)}" : "") + ".");
            }
        }
        return said;
    }
}
