using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sysoptimizer.Services;

namespace Sysoptimizer.Mcp;

/// <summary>The tools Claude sees. All read-only, all answer in plain text over the recorded history.</summary>
public static class Tools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string RangeHelp = "Time range: either last_minutes, or from/to as local ISO date-times like 2026-10-05T14:00. Default: the last 60 minutes.";
    private const string SampleNote = "Only the heaviest apps are recorded each time (top by CPU, memory and GPU every 10 s), so small background apps may be missing.";

    private static readonly string[] Metrics = { "cpu", "mem", "gpu", "cpu_temp", "gpu_temp", "disk", "net", "cpu_power", "gpu_power", "fan", "battery" };

    // The params constructor, not a collection initializer: the initializer binds to the generic Add<T>,
    // which the trimmer (rightly, in general) flags as unsafe.
    public static JsonArray Definitions() => new(
        Tool("get_current_usage", "What the PC is doing right now: the latest CPU, memory, GPU and temperature reading and the apps using the most.", new JsonObject()),
        Tool("get_usage_summary", $"CPU, memory, GPU and temperature over a time range: averages, peaks with their time, and a timeline. {RangeHelp}", RangeProps()),
        Tool("get_top_processes", $"Which apps used the most CPU, memory or GPU over a time range. {SampleNote} {RangeHelp}", RangeProps(new JsonObject
        {
            ["sort_by"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("cpu", "ram", "gpu"), ["description"] = "Rank by average CPU (default), memory or GPU." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "How many apps (default 10, max 50)." },
        })),
        Tool("find_spikes", $"Find periods when a metric went above a threshold, and which apps were running then — answers 'what made my PC slow/loud/hot at …'. {RangeHelp}", RangeProps(new JsonObject
        {
            ["metric"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Metrics.Select(m => (JsonNode)m).ToArray()), ["description"] = "Which metric (default cpu)." },
            ["threshold"] = new JsonObject { ["type"] = "number", ["description"] = "Percent for usage and battery, °C for temperatures, MB/s for disk, Mbps for network, W for power, RPM for fans. Default 80 (85 for temperatures)." },
            ["min_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Ignore spikes shorter than this (default 3)." },
        })),
        Tool("get_app_history", $"When a given app was running and how much it used, session by session. {SampleNote} {RangeHelp}", RangeProps(new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Process name or part of it, e.g. 'chrome' or 'Cyberpunk'." },
        }, required: "name")),
        Tool("get_events", $"What happened on the PC over a time range: apps launched (outside Windows' own folder), sleep and wake, and each time Sysoptimizer started (with when the PC booted). {RangeHelp}", RangeProps()));

    private static JsonObject Tool(string name, string description, JsonObject properties, string? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required != null) schema["required"] = new JsonArray(required);
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema, ["annotations"] = new JsonObject { ["readOnlyHint"] = true } };
    }

    private static JsonObject RangeProps(JsonObject? extra = null, string? required = null)
    {
        var props = new JsonObject
        {
            ["last_minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "Look back this many minutes from now." },
            ["from"] = new JsonObject { ["type"] = "string", ["description"] = "Start, local ISO date-time." },
            ["to"] = new JsonObject { ["type"] = "string", ["description"] = "End, local ISO date-time (default now)." },
        };
        foreach (var (k, v) in extra ?? new JsonObject()) props[k] = v?.DeepClone();
        return props;
    }

    public static JsonObject Call(string name, JsonObject args)
    {
        try
        {
            string text = name switch
            {
                "get_current_usage" => CurrentUsage(),
                "get_usage_summary" => UsageSummary(args),
                "get_top_processes" => TopProcesses(args),
                "find_spikes" => FindSpikes(args),
                "get_app_history" => AppHistory(args),
                "get_events" => Events(args),
                _ => throw new ArgumentException($"Unknown tool: {name}"),
            };
            return Result(text, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Result(ex.Message, isError: true);
        }
    }

    private static JsonObject Result(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    // --- tools ---

    private static string CurrentUsage()
    {
        var (system, processes, _) = HistoryStore.Read(DateTime.Now.AddMinutes(-2), DateTime.Now);
        if (system.Count == 0) return NotRecording();
        var s = system[^1];
        var sb = new StringBuilder();
        sb.AppendLine($"As of {Local(s.Time)} ({Ago(s.Time)}):");
        sb.AppendLine($"CPU {Pct(s.Cpu)} · Memory {Pct(s.Mem)} · GPU {Pct(s.Gpu)} · CPU {Deg(s.CpuTemp)} · GPU {Deg(s.GpuTemp)}"
            + (float.IsNaN(s.DiskMBs) ? "" : $" · disk {Fmt(s.DiskMBs, "disk")} · network {Fmt(s.NetMbps, "net")}"));
        if (processes.Count > 0)
        {
            var latest = processes.Max(p => p.Time);
            sb.AppendLine($"Top apps at {Local(latest)}:");
            foreach (var p in processes.Where(p => p.Time == latest).OrderByDescending(p => p.Cpu))
                sb.AppendLine($"- {p.Name}: CPU {Pct(p.Cpu)}, memory {MB(p.RamMB)}, GPU {Pct(p.Gpu)}");
        }
        return sb.ToString();
    }

    private static string UsageSummary(JsonObject args)
    {
        var (from, to) = Range(args);
        var (system, _, _) = Load(from, to);
        if (system.Count == 0) return NoData(from, to);

        var sb = new StringBuilder();
        sb.AppendLine($"{Local(from)} – {Local(to)} · {Coverage(system, from, to)}{Resolution(from, to)}");
        foreach (var metric in Metrics)
        {
            var values = system.Where(s => !float.IsNaN(Value(s, metric))).ToList();
            if (values.Count == 0) { sb.AppendLine($"{Label(metric)}: not recorded"); continue; }
            var peak = values.MaxBy(s => Value(s, metric));
            sb.AppendLine($"{Label(metric)}: average {Fmt(values.Average(s => Value(s, metric)), metric)}, min {Fmt(values.Min(s => Value(s, metric)), metric)}, peak {Fmt(Value(peak, metric), metric)} at {Local(peak.Time)}");
        }

        int buckets = Math.Clamp((int)((to - from).TotalMinutes / 5), 1, 48);
        double size = (to - from).TotalSeconds / buckets;
        var fromUtc = from.ToUniversalTime();
        sb.AppendLine().AppendLine("Timeline (average / peak):");
        foreach (var bucket in system.GroupBy(s => (int)((s.Time - fromUtc).TotalSeconds / size)).OrderBy(g => g.Key))
        {
            var start = from.AddSeconds(bucket.Key * size);
            string Avg(string m) => bucket.Any(s => !float.IsNaN(Value(s, m))) ? Fmt(bucket.Where(s => !float.IsNaN(Value(s, m))).Average(s => Value(s, m)), m) : "—";
            string Max(string m) => bucket.Any(s => !float.IsNaN(Value(s, m))) ? Fmt(bucket.Where(s => !float.IsNaN(Value(s, m))).Max(s => Value(s, m)), m) : "—";
            sb.AppendLine($"{start:HH:mm}  CPU {Avg("cpu")}/{Max("cpu")} · MEM {Avg("mem")} · GPU {Avg("gpu")}/{Max("gpu")} · temps peak CPU {Max("cpu_temp")} GPU {Max("gpu_temp")} · disk peak {Max("disk")} · net peak {Max("net")}");
        }
        return sb.ToString();
    }

    private static string TopProcesses(JsonObject args)
    {
        var (from, to) = Range(args);
        string sortBy = (string?)args["sort_by"] ?? "cpu";
        int limit = Math.Clamp((int?)args["limit"] ?? 10, 1, 50);
        var (_, processes, _) = Load(from, to);
        if (processes.Count == 0) return NoData(from, to);

        var sb = new StringBuilder($"Top apps by average {sortBy}, {Local(from)} – {Local(to)}:\n");
        foreach (var t in HistoryStore.TopProcesses(processes, sortBy, limit))
            sb.AppendLine($"- {t.Name}: CPU avg {t.AvgCpu:0.0}% peak {t.PeakCpu:0}% · memory avg {MB(t.AvgRamMB)} peak {MB(t.PeakRamMB)} · GPU avg {t.AvgGpu:0.0}% peak {t.PeakGpu:0}%");
        return sb.AppendLine().Append(SampleNote).ToString();
    }

    private static string FindSpikes(JsonObject args)
    {
        var (from, to) = Range(args);
        string metric = (string?)args["metric"] ?? "cpu";
        if (!Metrics.Contains(metric)) throw new ArgumentException($"metric must be one of {string.Join(", ", Metrics)}");
        double threshold = (double?)args["threshold"] ?? (metric.EndsWith("temp") ? 85 : 80);
        int minSeconds = (int?)args["min_seconds"] ?? 3;
        // Always judged on the per-second data, so a 15-second burst gets its real start, end and apps. Over
        // long ranges the minute index (which keeps each minute's peak) only picks which days to read.
        List<SysSample> system;
        List<ProcSample> processes;
        if (Minutely(from, to))
        {
            var index = HistoryStore.ReadMinutes(from, to, systemOnly: true).System;
            if (index.Count == 0) return NoData(from, to);
            system = new();
            processes = new();
            foreach (var day in index.Where(s => Value(s, metric) >= threshold).Select(s => s.Time.ToLocalTime().Date).Distinct())
            {
                var data = HistoryStore.Read(day < from ? from : day, day.AddDays(1) > to ? to : day.AddDays(1).AddSeconds(-1));
                system.AddRange(data.System);
                processes.AddRange(data.Processes);
            }
        }
        else
        {
            (system, processes, _) = HistoryStore.Read(from, to);
            if (system.Count == 0) return NoData(from, to);
        }

        var spikes = new List<(DateTime Start, DateTime End, SysSample Peak)>();
        DateTime? start = null, last = null;
        SysSample peak = default;
        foreach (var s in system)
        {
            float v = Value(s, metric);
            bool above = !float.IsNaN(v) && v >= threshold;
            bool gap = last != null && (s.Time - last.Value).TotalSeconds > 5; // the recorder wasn't running
            if (start != null && (!above || gap))
            {
                spikes.Add((start.Value, last!.Value, peak));
                start = null;
            }
            if (above)
            {
                if (start == null) { start = s.Time; peak = s; }
                else if (v > Value(peak, metric)) peak = s;
            }
            last = s.Time;
        }
        if (start != null) spikes.Add((start.Value, last!.Value, peak));
        spikes = spikes.Where(sp => (sp.End - sp.Start).TotalSeconds + 1 >= minSeconds).ToList();

        if (spikes.Count == 0) return $"{Label(metric)} never stayed at or above {Fmt(threshold, metric)} for {minSeconds}s between {Local(from)} and {Local(to)}.";

        string rankBy = metric switch { "mem" => "ram", "gpu" or "gpu_temp" => "gpu", _ => "cpu" };
        var sb = new StringBuilder($"{spikes.Count} period(s) with {Label(metric)} ≥ {Fmt(threshold, metric)}" + (spikes.Count > 25 ? ", the 25 biggest shown" : "") + ":\n");
        foreach (var sp in spikes.OrderByDescending(sp => Value(sp.Peak, metric)).Take(25).OrderBy(sp => sp.Start))
        {
            var during = processes.Where(p => p.Time >= sp.Start.AddSeconds(-10) && p.Time <= sp.End.AddSeconds(10)).ToList();
            var top = HistoryStore.TopProcesses(during, rankBy, 5);
            sb.AppendLine($"- {Local(sp.Start)} → {sp.End.ToLocalTime():HH:mm:ss} ({Duration(sp.End - sp.Start)}), peak {Fmt(Value(sp.Peak, metric), metric)} at {sp.Peak.Time.ToLocalTime():HH:mm:ss}"
                + $" · CPU {Pct(sp.Peak.Cpu)} MEM {Pct(sp.Peak.Mem)} GPU {Pct(sp.Peak.Gpu)} {Deg(sp.Peak.CpuTemp)}/{Deg(sp.Peak.GpuTemp)}");
            sb.AppendLine(top.Count == 0
                ? "    apps: none recorded in this window"
                : "    apps: " + string.Join(", ", top.Select(t => $"{t.Name} (CPU {t.PeakCpu:0}%, {MB(t.PeakRamMB)}, GPU {t.PeakGpu:0}%)")));
        }
        return sb.ToString();
    }

    private static string AppHistory(JsonObject args)
    {
        var (from, to) = Range(args);
        string name = (string?)args["name"] ?? throw new ArgumentException("name is required");
        var (_, processes, _) = Load(from, to);
        var mine = processes.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Time).ToList();
        if (mine.Count == 0) return $"No app matching '{name}' among the recorded top apps between {Local(from)} and {Local(to)}. {SampleNote}";

        // Snapshots come every 10 s; more than a minute apart means the app was idle (or closed) in between.
        var sessions = new List<List<ProcSample>>();
        foreach (var p in mine)
        {
            if (sessions.Count == 0 || (p.Time - sessions[^1][^1].Time).TotalSeconds > Math.Max(60, GapSeconds(from, to))) sessions.Add(new());
            sessions[^1].Add(p);
        }

        var sb = new StringBuilder($"{string.Join(", ", mine.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase))}: {sessions.Count} session(s) between {Local(from)} and {Local(to)}" + (sessions.Count > 30 ? ", the latest 30 shown" : "") + ":\n");
        foreach (var s in sessions.TakeLast(30))
            sb.AppendLine($"- {Local(s[0].Time)} → {s[^1].Time.ToLocalTime():HH:mm:ss} ({Duration(s[^1].Time - s[0].Time)}): CPU avg {s.Average(p => Z(p.Cpu)):0.0}% peak {s.Max(p => Z(p.Cpu)):0}% · memory peak {MB(s.Max(p => Z(p.RamMB)))} · GPU peak {s.Max(p => Z(p.Gpu)):0}%");
        return sb.AppendLine().Append(SampleNote).ToString();
    }

    private static string Events(JsonObject args)
    {
        var (from, to) = Range(args);
        var events = Load(from, to).Events;
        if (events.Count == 0) return $"No events recorded between {Local(from)} and {Local(to)}.";
        var sb = new StringBuilder($"{events.Count} event(s) between {Local(from)} and {Local(to)}" + (events.Count > 200 ? ", the latest 200 shown" : "") + ":\n");
        foreach (var e in events.TakeLast(200))
        {
            string what = e.Kind switch
            {
                "launch" => $"launched {e.Detail}",
                "sleep" => "PC went to sleep",
                "wake" => "PC woke up",
                "start" when long.TryParse(e.Detail, out long boot) => $"Sysoptimizer started (PC booted {Local(DateTimeOffset.FromUnixTimeSeconds(boot).UtcDateTime)})",
                _ => $"{e.Kind} {e.Detail}".Trim(),
            };
            sb.AppendLine($"- {Local(e.Time)}  {what}");
        }
        return sb.ToString();
    }

    // --- helpers ---

    /// <summary>Up to two days at full resolution; beyond that the per-minute summaries (peaks per minute).</summary>
    private static bool Minutely(DateTime from, DateTime to) => (to - from).TotalHours > 48;
    private static HistoryData Load(DateTime from, DateTime to) => Minutely(from, to) ? HistoryStore.ReadMinutes(from, to) : HistoryStore.Read(from, to);
    private static double GapSeconds(DateTime from, DateTime to) => Minutely(from, to) ? 150 : 5;
    private static string Resolution(DateTime from, DateTime to) => Minutely(from, to) ? " · per-minute resolution (each value is that minute's peak)" : "";

    private static (DateTime From, DateTime To) Range(JsonObject args)
    {
        DateTime to = args["to"] is JsonNode t ? ParseTime((string?)t) : DateTime.Now;
        DateTime from = args["from"] is JsonNode f ? ParseTime((string?)f)
                      : to.AddMinutes(-Math.Clamp((int?)args["last_minutes"] ?? 60, 1, HistoryStore.MaxRetentionDays * 1440));
        if (from >= to) throw new ArgumentException("from must be before to");
        return (from, to);
    }

    private static DateTime ParseTime(string? s) =>
        DateTime.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out var d) ? d.ToLocalTime()
        : throw new ArgumentException($"Can't read '{s}' as a date-time — use ISO like 2026-10-05T14:00.");

    private static float Value(SysSample s, string metric) => metric switch
    {
        "mem" => s.Mem, "gpu" => s.Gpu, "cpu_temp" => s.CpuTemp, "gpu_temp" => s.GpuTemp, "disk" => s.DiskMBs, "net" => s.NetMbps, "cpu_power" => s.CpuWatts, "gpu_power" => s.GpuWatts, "fan" => s.FanRpm, "battery" => s.BatteryPct, _ => s.Cpu,
    };

    private static string Label(string metric) => metric switch
    {
        "mem" => "Memory", "gpu" => "GPU", "cpu_temp" => "CPU temperature", "gpu_temp" => "GPU temperature",
        "disk" => "Disk (read + write)", "net" => "Network (down + up)", "cpu_power" => "CPU power", "gpu_power" => "GPU power",
        "fan" => "Fastest fan", "battery" => "Battery", _ => "CPU",
    };

    private static string Fmt(double v, string metric) => metric switch
    {
        "disk" => $"{v:0.#} MB/s", "net" => $"{v:0.#} Mbps", "cpu_power" or "gpu_power" => $"{v:0} W", "fan" => $"{v:0} RPM",
        _ when metric.EndsWith("temp") => $"{v:0}°C", _ => $"{v:0}%",
    };
    private static string Pct(float v) => float.IsNaN(v) ? "n/a" : $"{v:0}%";
    private static string Deg(float v) => float.IsNaN(v) ? "n/a" : $"{v:0}°C";
    private static string MB(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    private static double Z(float v) => float.IsNaN(v) ? 0 : v;
    private static string Local(DateTime t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);
    private static string Duration(TimeSpan d) => d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{(int)d.TotalSeconds + 1}s";

    private static string Ago(DateTime t)
    {
        var d = DateTime.UtcNow - t.ToUniversalTime();
        return d.TotalSeconds < 90 ? $"{d.TotalSeconds:0}s ago" : $"{d.TotalMinutes:0} min ago";
    }

    private static string Coverage(List<SysSample> system, DateTime from, DateTime to)
    {
        double perSample = Minutely(from, to) ? 60 : 1;
        double recorded = Math.Min(100, system.Count * perSample / Math.Max(1, (to - from).TotalSeconds) * 100);
        return recorded > 95 ? "recorded throughout" : $"recorded {recorded:0}% of the time (gaps = Sysoptimizer wasn't running)";
    }

    private static string NoData(DateTime from, DateTime to) =>
        $"Nothing recorded between {Local(from)} and {Local(to)}. Sysoptimizer only records while it's running — turn on 'Record in background' in the app to keep it recording from logon.";

    private static string NotRecording() =>
        "No reading in the last 2 minutes — Sysoptimizer isn't running right now. Open it, or turn on 'Record in background' so it records from logon.";
}
