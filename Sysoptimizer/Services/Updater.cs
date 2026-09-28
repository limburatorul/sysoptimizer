using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Sysoptimizer.Services;

// Self-update, the way File Labs and Shelf do it (vault: File Labs/Release si update.md):
//   • check 3 s after launch and every 10 minutes — the app stays open for a while, and a release
//     published in the morning would otherwise be invisible until a restart;
//   • "Later" silences that one version for this session only, and a manual check ignores it, because
//     asking explicitly deserves an answer;
//   • updating downloads the release's installer, checks its size and SHA-256 against what GitHub
//     published for the asset, then hands off to a detached one-shot .cmd and quits: nothing in this
//     process can outlive the installer that is about to overwrite it. The script waits for this PID,
//     runs the installer silently, and starts the app again.
public static class Updater
{
    public const string Repo = "limburatorul/sysoptimizer";

    public record Release(Version Version, string Tag, string Notes, string AssetUrl, long AssetSize, string? Sha256);

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);
    private static string? _dismissed; // tag the user said "Later" to; deliberately not persisted

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The latest release when it is newer than what's running, else null.</summary>
    public static async Task<Release?> Check(bool manual)
    {
        var release = await Latest();
        if (release == null) return null;
        if (release.Version <= Current) return null;
        if (!manual && release.Tag == _dismissed) return null; // "Later" must not become a nag
        return release;
    }

    /// <summary>The latest release as published, whatever its version.</summary>
    public static async Task<Release?> Latest()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("Sysoptimizer");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object) return null;
            // GitHub publishes each asset's SHA-256 as "sha256:<hex>"; older assets have none.
            var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return new Release(version, tag, root.GetProperty("body").GetString() ?? "",
                asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                digest?.StartsWith("sha256:") == true ? digest[7..] : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null; // offline, rate-limited, or a release without an installer: nothing to say
        }
    }

    public static void Dismiss(Release release) => _dismissed = release.Tag;

    /// <summary>Downloads the installer, reporting 0..1 progress. Returns its path.</summary>
    public static async Task<string> Download(Release release, IProgress<double>? progress, CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Sysoptimizer-{release.Version}-setup.exe");
        using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.AssetSize;
            using var source = await response.Content.ReadAsStreamAsync(token);
            using var file = File.Create(path);
            var buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), token);
                done += n;
                progress?.Report(total > 0 ? (double)done / total : 0);
            }
        }
        // Size check: a truncated download would otherwise be run as an installer.
        var length = new FileInfo(path).Length;
        if (length != release.AssetSize)
        {
            File.Delete(path);
            throw new IOException($"Download is {CleanupService.FormatSize(length)}, expected {CleanupService.FormatSize(release.AssetSize)}");
        }
        // The size catches a cut-off download; the checksum catches everything else. This file is about
        // to be run as an installer, so one that differs from what was published never is.
        if (release.Sha256 != null)
        {
            string actual;
            using (var file = File.OpenRead(path)) actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new IOException("The download doesn't match the checksum GitHub published for it");
            }
        }
        return path;
    }

    /// <summary>Hands off to a detached script and returns; the caller then quits so the installer can replace us.</summary>
    public static void InstallAndRestart(string installer)
    {
        var exe = Environment.ProcessPath;
        var script = Path.Combine(Path.GetTempPath(), $"sysoptimizer-update-{Environment.ProcessId}.cmd");
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /FI "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            "{installer}" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
            start "" "{exe}"
            del "%~f0"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
    }
}
