using System.IO;
using Microsoft.Win32;

namespace Sysoptimizer.Services;

public sealed record PrivacyAccess(string Capability, string App, string Id, DateTime? LastStart, DateTime? LastStop)
{
    public bool InUse => LastStart != null && LastStop == null;
}

/// <summary>
/// Who used the camera, microphone or location, and when — read from the log Windows itself keeps for the
/// privacy indicator (CapabilityAccessManager\ConsentStore). Desktop apps sit under "NonPackaged", their
/// full path with '\' written as '#'; Store apps are named by package.
/// </summary>
public static class PrivacyService
{
    private const string StorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly (string Key, string Label)[] Capabilities = { ("webcam", "Camera"), ("microphone", "Microphone"), ("location", "Location") };

    public static List<PrivacyAccess> GetAccessLog()
    {
        var result = new List<PrivacyAccess>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var (capKey, label) in Capabilities)
        {
            using var cap = hive.OpenSubKey($@"{StorePath}\{capKey}");
            if (cap == null) continue;
            foreach (var name in cap.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var nonPackaged = cap.OpenSubKey(name);
                    foreach (var exeKey in nonPackaged?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        string path = exeKey.Replace('#', '\\');
                        Add(result, nonPackaged!, exeKey, label, Path.GetFileNameWithoutExtension(path), path);
                    }
                }
                else
                {
                    Add(result, cap, name, label, name.Split('_')[0], name);
                }
            }
        }
        return result.OrderByDescending(a => a.InUse).ThenByDescending(a => a.LastStart).ToList();
    }

    private static void Add(List<PrivacyAccess> result, RegistryKey parent, string subKey, string capability, string app, string id)
    {
        using var key = parent.OpenSubKey(subKey);
        var start = FromFileTime(key?.GetValue("LastUsedTimeStart"));
        if (start == null) return; // allowed but never actually used — nothing to show
        result.Add(new PrivacyAccess(capability, app, id, start, FromFileTime(key?.GetValue("LastUsedTimeStop"))));
    }

    private static DateTime? FromFileTime(object? value) => value is long ft && ft > 0 ? DateTime.FromFileTime(ft) : null;
}
