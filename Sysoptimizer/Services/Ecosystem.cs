using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sysoptimizer.Services;

/// <summary>
/// The folder the Protagonist Labs apps share on this PC (%APPDATA%\ProtagonistLabs): each app writes its
/// card in apps\, and what it offers the others in data\. Sysoptimizer writes its card, and for Shelf the
/// games' sessions from the recorded history (data\sysoptimizer.games.json); it reads the other apps' cards
/// for About → Works with. Anything read from here may be missing or broken — that's just "not there".
/// Kept free of WPF, so the self-test can run it.
/// </summary>
public static class Ecosystem
{
    /// <summary>Settable for the self-test. The app runs elevated as the signed-in user, so this is that user's Roaming folder.</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs");
    public const string Page = "https://protagonistlabs.app/sysoptimizer/";
    private const int Days = 30;

    public static void WriteCard(string exe, Version version) => WriteJson(Path.Combine(Folder, "apps", "sysoptimizer.json"), new JsonObject
    {
        ["id"] = "sysoptimizer", ["name"] = "Sysoptimizer", ["version"] = version.ToString(3), ["exe"] = exe,
        ["edition"] = "free", ["paid"] = false, ["page"] = Page, ["commands"] = new JsonObject(),
    });

    /// <summary>Another app's card, or null when it's missing, unreadable or its exe is gone (uninstalled).</summary>
    public static JsonObject? Card(string id) =>
        Read(Path.Combine(Folder, "apps", id + ".json")) is { } card && card["exe"] is JsonValue v && v.TryGetValue(out string? exe) && File.Exists(exe) ? card : null;

    /// <summary>Someone bought a Protagonist Labs app: the "30% off for you" line may be shown.</summary>
    public static bool BoughtSomething()
    {
        try
        {
            return Directory.GetFiles(Path.Combine(Folder, "apps"), "*.json").Any(f =>
                Card(Path.GetFileNameWithoutExtension(f)) is { } c && c["paid"] is JsonValue p && p.TryGetValue(out bool paid) && paid
                && c["edition"] is JsonValue e && e.TryGetValue(out string? edition) && edition == "full");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A shared file as JSON, or null if it isn't a schema-1 object.</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o
                   && o["schema"] is JsonValue v && v.TryGetValue(out int schema) && schema == 1 ? o : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    private static void WriteJson(string path, JsonObject body)
    {
        var file = new JsonObject { ["schema"] = 1, ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        foreach (var (key, value) in body.ToList()) { body.Remove(key); file[key] = value; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); // UTF-8, no BOM
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // --- Shelf's games, as the history saw them ---

    /// <summary>One game on one local day: the minutes it was among the recorded apps, and its frame rate then.</summary>
    public sealed record Session(string Exe, string Name, DateTime Date, int Minutes, double? AvgFps, double? MaxFps);

    private sealed class DayStats { public HashSet<long> Minutes = new(); public double FpsSum; public int FpsCount; public double FpsMax; }

    private static readonly object Gate = new();
    // Finished days never change: read once per run (a raw day is ~7 MB of text), today again each time.
    private static readonly Dictionary<DateTime, Dictionary<string, DayStats>> FinishedDays = new();
    private static string _cachedFor = "";

    /// <summary>Writes data\sysoptimizer.games.json for the games in Shelf's shelf.games.json; nothing without it.</summary>
    public static void WriteGames()
    {
        var games = ShelfGames();
        if (games.Count == 0) return; // no Shelf, or no games: nothing to share
        lock (Gate) // the hourly write and the one on exit can meet
        {
            WriteJson(Path.Combine(Folder, "data", "sysoptimizer.games.json"), new JsonObject
            {
                ["sessions"] = new JsonArray(Sessions(games, DateTime.Today).Select(s => (JsonNode)new JsonObject
                {
                    ["exe"] = s.Exe, ["name"] = s.Name, ["date"] = s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["minutes"] = s.Minutes, ["avgFps"] = s.AvgFps, ["maxFps"] = s.MaxFps,
                }).ToArray()),
            });
        }
    }

    /// <summary>(exe, name) of each game in shelf.games.json that has an exe.</summary>
    public static List<(string Exe, string Name)> ShelfGames()
    {
        var list = new List<(string, string)>();
        if (Read(Path.Combine(Folder, "data", "shelf.games.json"))?["games"] is not JsonArray games) return list;
        foreach (var g in games.OfType<JsonObject>())
            if (g["exe"] is JsonValue e && e.TryGetValue(out string? exe) && !string.IsNullOrWhiteSpace(exe))
                list.Add((exe, g["name"] is JsonValue n && n.TryGetValue(out string? name) ? name : Path.GetFileNameWithoutExtension(exe)));
        return list;
    }

    /// <summary>
    /// The games' sessions over the last 30 days, from the apps recorded every 10 s (P rows carry the process
    /// name without .exe, so a game matches on its exe's file name). Only the top apps are recorded, which a
    /// running game nearly always is (memory, GPU or frame rate).
    /// </summary>
    public static List<Session> Sessions(List<(string Exe, string Name)> games, DateTime today)
    {
        var names = games.Select(g => Path.GetFileNameWithoutExtension(g.Exe)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<Session>();
        lock (Gate)
        {
            string key = string.Join('|', names.Order(StringComparer.OrdinalIgnoreCase));
            if (key != _cachedFor) { FinishedDays.Clear(); _cachedFor = key; } // another set of games: count again
            for (var day = today.AddDays(1 - Days); day <= today; day = day.AddDays(1))
            {
                if (!FinishedDays.TryGetValue(day, out var stats))
                {
                    stats = ReadDay(day, names, out bool whole);
                    if (day < today && whole) FinishedDays[day] = stats;
                }
                foreach (var (exe, name) in games)
                    if (stats.TryGetValue(Path.GetFileNameWithoutExtension(exe), out var s))
                        sessions.Add(new Session(exe, name, day, s.Minutes.Count,
                            s.FpsCount > 0 ? Math.Round(s.FpsSum / s.FpsCount, 1) : null, s.FpsCount > 0 ? Math.Round(s.FpsMax, 1) : null));
            }
            foreach (var old in FinishedDays.Keys.Where(d => d <= today.AddDays(-Days)).ToList()) FinishedDays.Remove(old);
        }
        return sessions;
    }

    private static Dictionary<string, DayStats> ReadDay(DateTime day, HashSet<string> names, out bool whole)
    {
        whole = false;
        var stats = new Dictionary<string, DayStats>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = HistoryStore.OpenDay(day, minutes: false);
            if (reader == null) { whole = true; return stats; }
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("P,")) continue; // most lines are the per-second S rows
                var f = line.Split(',');
                if (f.Length < 6 || !names.Contains(f[2]) || !long.TryParse(f[1], CultureInfo.InvariantCulture, out long t)) continue;
                if (!stats.TryGetValue(f[2], out var s)) stats[f[2]] = s = new DayStats();
                s.Minutes.Add(t / 60);
                if (f.Length > 6 && double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps >= 1)
                {
                    s.FpsSum += fps; s.FpsCount++; s.FpsMax = Math.Max(s.FpsMax, fps);
                }
            }
            whole = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { } // a day being packed right now: read again next time
        return stats;
    }
}
