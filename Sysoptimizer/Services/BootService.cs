using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace Sysoptimizer.Services;

public sealed record SlowStarter(string Name, double ExtraSeconds);

/// <param name="Seconds">From power-on to a usable desktop with startup apps settled (Windows' "boot time").</param>
/// <param name="DesktopSeconds">To the desktop alone (Windows' "main path").</param>
public sealed record Boot(DateTime StartUtc, double Seconds, double DesktopSeconds, int StartupApps, List<SlowStarter> SlowedBy);

/// <summary>
/// How long each Windows start took and what held it up, from Windows' own boot diagnostics
/// (Diagnostics-Performance log: 100 = a boot, 101/102/103 = an app, driver or service that took
/// longer than usual). Reading it needs admin, which the app has.
/// </summary>
public static class BootService
{
    private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>The most recent boots, newest first; empty when Windows keeps no such log.</summary>
    public static List<Boot> Read(int max = 30)
    {
        var events = new List<(int Id, Dictionary<string, string> Data)>();
        try
        {
            var query = new EventLogQuery(Log, PathType.LogName, "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? record; (record = reader.ReadEvent()) != null;)
                using (record)
                {
                    var data = XDocument.Parse(record.ToXml()).Descendants().Where(e => e.Name.LocalName == "Data")
                        .GroupBy(e => (string?)e.Attribute("Name") ?? "").ToDictionary(g => g.Key, g => g.First().Value);
                    events.Add((record.Id, data));
                    if (events.Count(e => e.Id == 100) >= max) break;
                }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { return new(); } // no such log here, or it was cleared
        return Group(events);
    }

    /// <summary>Each boot with the slow starters that share its start time.</summary>
    private static List<Boot> Group(List<(int Id, Dictionary<string, string> Data)> events)
    {
        static double Seconds(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ? ms / 1000 : 0;
        var slow = events.Where(e => e.Id != 100 && e.Data.ContainsKey("StartTime"))
            .ToLookup(e => e.Data["StartTime"], e => new SlowStarter(
                e.Data.GetValueOrDefault("FriendlyName") is { Length: > 0 } friendly ? friendly : e.Data.GetValueOrDefault("Name") ?? "?",
                Seconds(e.Data, "DegradationTime")));
        return events.Where(e => e.Id == 100 && e.Data.ContainsKey("BootStartTime"))
            .Select(e => new Boot(
                DateTime.TryParse(e.Data["BootStartTime"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at) ? at : DateTime.MinValue,
                Seconds(e.Data, "BootTime"), Seconds(e.Data, "MainPathBootTime"),
                int.TryParse(e.Data.GetValueOrDefault("BootNumStartupApps"), out int apps) ? apps : 0,
                slow[e.Data["BootStartTime"]].OrderByDescending(s => s.ExtraSeconds).ToList()))
            .ToList();
    }
}
