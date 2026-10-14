using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace Sysoptimizer.Services;

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

    /// <summary>
    /// Windows updates installed since a time (System log, WindowsUpdateClient 19 — without the antivirus
    /// definitions that arrive several times a day), and drivers set up for a device (Kernel-PnP 400) that
    /// aren't Windows' own and are new for that device or a new version: (when, kind, what). A device that's
    /// merely configured again with the driver it had — a VPN adapter at each connection — counts for nothing;
    /// <paramref name="drivers"/> (device → driver version) is the record of what each had, kept up to date here.
    /// </summary>
    public static List<(DateTime Utc, string Kind, string Detail)> WindowsChanges(DateTime sinceUtc, Dictionary<string, string> drivers)
    {
        var changes = new List<(DateTime, string, string)>();
        string since = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        void Read(string log, string filter, Action<DateTime, Dictionary<string, string>> found)
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[{filter} and TimeCreated[@SystemTime>='{since}']]]"));
                for (EventRecord? record; (record = reader.ReadEvent()) != null;)
                    using (record)
                        found(record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, XDocument.Parse(record.ToXml()).Descendants().Where(e => e.Name.LocalName == "Data")
                            .GroupBy(e => (string?)e.Attribute("Name") ?? "").ToDictionary(g => g.Key, g => g.First().Value));
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { } // no such log, or not readable here
        }
        var updates = new HashSet<string>();
        Read("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19", (at, data) =>
        {
            string title = data.GetValueOrDefault("updateTitle") ?? "";
            if (title.Length == 0 || title.Contains("Security Intelligence Update") || title.Contains("Definition Update")) return;
            // A Store app's update is titled "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2": the app's name is enough.
            if (title.Length > 13 && title[12] == '-' && title[..12].All(char.IsLetterOrDigit)) title = "Store app " + title[13..];
            if (updates.Add(title)) changes.Add((at, "update", title));
        });
        Read("Microsoft-Windows-Kernel-PnP/Configuration", "EventID=400", (at, data) =>
        {
            if (data.GetValueOrDefault("DriverInbox") != "false" || data.GetValueOrDefault("DeviceInstanceId") is not { Length: > 0 } device) return;
            string version = data.GetValueOrDefault("DriverVersion") ?? "";
            if (drivers.TryGetValue(device, out var had) && had == version) return;
            drivers[device] = version;
            string package = data.GetValueOrDefault("DriverPackageId") is { } id && id.IndexOf(".inf", StringComparison.OrdinalIgnoreCase) is > 0 and var end ? id[..end] : data.GetValueOrDefault("DriverName") ?? "driver";
            changes.Add((at, "driver", $"{data.GetValueOrDefault("DriverProvider")} {package} {version}".Trim()));
        });
        return changes;
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
