using System.IO;

namespace Sysoptimizer.Services;

/// <summary>
/// What changed on the PC, written into the history as events the History chart marks: apps installed,
/// removed or updated (the installed-programs list against the last one seen), Windows updates and new or
/// updated drivers (from Windows' own logs, see <see cref="BootService.WindowsChanges"/>). Each list seen is
/// kept on disk, so a change made while Sysoptimizer was off is still noticed at its next start.
/// </summary>
public static class ChangeService
{
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sysoptimizer");

    /// <summary>What appeared, went or changed version between two lists (name → version).</summary>
    public static List<(string Kind, string Detail)> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> now)
    {
        static string Named(string name, string version) => version.Length > 0 ? $"{name} {version}" : name;
        var changes = new List<(string, string)>();
        foreach (var (name, version) in now.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            if (!before.TryGetValue(name, out var old)) changes.Add(("installed", Named(name, version)));
            else if (old != version && version.Length > 0) changes.Add(("updated", $"{name} {(old.Length > 0 ? old + " → " : "")}{version}"));
        foreach (var (name, version) in before.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            if (!now.ContainsKey(name)) changes.Add(("removed", Named(name, version)));
        return changes;
    }

    /// <summary>A list kept from last time ("name\tversion" lines), or null the first time.</summary>
    public static Dictionary<string, string>? Load(string name)
    {
        string file = Path.Combine(Folder, name);
        if (!File.Exists(file)) return null;
        var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(file))
            if (line.Split('\t') is [var key, var version]) list[key] = version;
        return list;
    }

    public static void Save(string name, IReadOnlyDictionary<string, string> list)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllLines(Path.Combine(Folder, name), list.Select(p => $"{p.Key.Replace('\t', ' ')}\t{p.Value.Replace('\t', ' ')}"));
    }
}
