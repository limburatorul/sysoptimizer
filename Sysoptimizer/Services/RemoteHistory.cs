using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sysoptimizer.Services;

/// <summary>
/// Watching another PC's history without remote-desktopping into it. The watched PC runs a small read-only
/// HTTP server; the watcher holds the same 256-bit access key.
///   • every request is signed: HMAC-SHA256 over its path and a timestamp, so without the key nothing is
///     answered, and a captured request stops working after two minutes;
///   • every answer is sealed with AES-GCM under a key derived from the access key, bound to the request
///     it answers — the LAN sees neither what ran nor when, and can't alter or replay it;
///   • besides the history, the key lets the watcher install apps here (winget, or an .exe/.msi it uploads —
///     sealed the same way, and each install request runs once); the firewall rule is for private networks only.
/// Free of WPF so the self-test can exercise it.
/// </summary>
public static class RemoteCrypto
{
    public const int KeyBytes = 32;
    private const int NonceBytes = 12, TagBytes = 16;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    public static string NewKey() => Base64Url(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>The key as typed or pasted, or null if it isn't one.</summary>
    public static byte[]? ParseKey(string text)
    {
        try
        {
            string s = text.Trim().Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Derive(byte[] key, string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: Encoding.UTF8.GetBytes(purpose));

    public static string Sign(byte[] key, string pathAndQuery, long unixTime) =>
        Convert.ToHexString(HMACSHA256.HashData(Derive(key, "sysoptimizer-remote-mac"), Encoding.UTF8.GetBytes($"{pathAndQuery}\n{unixTime}")));

    /// <summary>True when the signature matches and the timestamp is recent (both PCs keep Windows time).</summary>
    public static bool Verify(byte[] key, string pathAndQuery, string? time, string? signature, DateTime nowUtc)
    {
        if (!long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out long t) || signature == null) return false;
        if (Math.Abs((nowUtc - DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime).TotalSeconds) > MaxClockSkew.TotalSeconds) return false;
        byte[] expected = Encoding.ASCII.GetBytes(Sign(key, pathAndQuery, t)), given = Encoding.ASCII.GetBytes(signature.ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Gzips and encrypts an answer; the request it answers is bound in as associated data.</summary>
    public static byte[] Seal(byte[] key, string request, byte[] plain)
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(plain);
        byte[] data = packed.ToArray(), nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[NonceBytes + TagBytes + data.Length];
        using var aes = new AesGcm(Derive(key, "sysoptimizer-remote-enc"), TagBytes);
        aes.Encrypt(nonce, data, output.AsSpan(NonceBytes + TagBytes), output.AsSpan(NonceBytes, TagBytes), Encoding.UTF8.GetBytes(request));
        nonce.CopyTo(output, 0);
        return output;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered with, or not the answer to this request.</exception>
    public static byte[] Open(byte[] key, string request, byte[] sealedData)
    {
        if (sealedData.Length < NonceBytes + TagBytes) throw new CryptographicException("The answer is too short to be genuine.");
        var data = new byte[sealedData.Length - NonceBytes - TagBytes];
        using (var aes = new AesGcm(Derive(key, "sysoptimizer-remote-enc"), TagBytes))
            aes.Decrypt(sealedData.AsSpan(0, NonceBytes), sealedData.AsSpan(NonceBytes + TagBytes), sealedData.AsSpan(NonceBytes, TagBytes), data, Encoding.UTF8.GetBytes(request));
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gz.CopyTo(plain);
        return plain.ToArray();
    }
}

/// <summary>What a watcher asks to install: a winget package, or an uploaded installer run with its options.</summary>
public sealed record RemoteInstall(string? WingetId, string? FileName, byte[]? File, string Args)
{
    public const long MaxFileBytes = 512L << 20;
    private static readonly Regex WingetIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$");

    public static bool IsWingetId(string? id) => id != null && WingetIdPattern.IsMatch(id);

    /// <summary>The name if it's a bare .exe or .msi file name (no folders), else null.</summary>
    public static string? SafeFileName(string? name) =>
        name is { Length: > 4 and <= 128 } && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) ? name : null;
}

/// <summary>The watched PC's side: answers signed requests for its history, and installs what a watcher sends.</summary>
public sealed class HistoryServer : IDisposable
{
    public const string Prefix = "/sysoptimizer/v1/";
    private readonly HttpListener _listener = new();
    private readonly byte[] _key;
    private readonly Func<byte[]?>? _live;
    private readonly Func<RemoteInstall, string>? _install;
    private readonly Dictionary<string, DateTime> _ran = new(); // install requests already run, by signature
    private readonly object _installGate = new();               // one installer at a time

    /// <param name="live">The latest full reading (the Resources tab's), serialized; null until the first one.</param>
    /// <param name="install">Runs an install and returns its outcome ("ok"/"failed" line, then details).</param>
    /// <param name="host">"+" = every address (needs admin — the app always runs elevated); the self-test uses localhost.</param>
    public HistoryServer(int port, byte[] key, Func<byte[]?>? live = null, Func<RemoteInstall, string>? install = null, string host = "+")
    {
        (_key, _live, _install) = (key, live, install);
        _listener.Prefixes.Add($"http://{host}:{port}{Prefix}");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; } // stopped
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            string path = request.RawUrl ?? "";
            string? time = request.Headers["X-Time"];
            string? signature = request.Headers["X-Auth"];
            if (request.HttpMethod is not ("GET" or "POST") || !RemoteCrypto.Verify(_key, path, time, signature, DateTime.UtcNow))
            {
                response.StatusCode = 401; // no detail: the caller doesn't hold the key, or its clock is off
                return;
            }
            byte[]? body = request.HttpMethod == "POST" ? Install(request, path, time!, signature!)
                : request.Url!.AbsolutePath.EndsWith("/live") ? _live?.Invoke() : Answer(request.Url.AbsolutePath, request.QueryString);
            if (body == null) { response.StatusCode = 400; return; }
            var sealedBody = RemoteCrypto.Seal(_key, $"{path}\n{time}", body);
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = sealedBody.Length;
            response.OutputStream.Write(sealedBody);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or UnauthorizedAccessException)
        {
            try { response.StatusCode = 500; } catch (InvalidOperationException) { } // headers already sent
        }
        finally
        {
            try { response.Close(); } catch (HttpListenerException) { } // the client went away
        }
    }

    /// <summary>install?winget=id, or install?file=name&amp;args=… with the installer sealed in the body. Null = refused.</summary>
    private byte[]? Install(HttpListenerRequest request, string path, string time, string signature)
    {
        if (_install == null || !request.Url!.AbsolutePath.EndsWith("/install")) return null;
        lock (_ran)
        {
            foreach (var old in _ran.Where(r => DateTime.UtcNow - r.Value > 2 * RemoteCrypto.MaxClockSkew).Select(r => r.Key).ToList()) _ran.Remove(old);
            if (!_ran.TryAdd(signature.ToUpperInvariant(), DateTime.UtcNow)) return null; // the same request again: a replay
        }
        var query = request.QueryString;
        string args = query["args"] ?? "";
        if (args.Length > 1024) return null;
        RemoteInstall job;
        if (query["winget"] is { } id)
        {
            if (!RemoteInstall.IsWingetId(id)) return null;
            job = new RemoteInstall(id, null, null, "");
        }
        else if (RemoteInstall.SafeFileName(query["file"]) is { } name)
        {
            long length = request.ContentLength64;
            if (length <= 0 || length > RemoteInstall.MaxFileBytes + 4096) return null;
            var sealedFile = new byte[length];
            request.InputStream.ReadExactly(sealedFile);
            try { job = new RemoteInstall(null, name, RemoteCrypto.Open(_key, $"upload\n{path}\n{time}", sealedFile), args); }
            catch (CryptographicException) { return null; }
        }
        else return null;
        lock (_installGate) return Encoding.UTF8.GetBytes(_install(job));
    }

    /// <summary>info → name and oldest day; day → that day's lines (raw ones after a time, or the minute summary).</summary>
    private static byte[]? Answer(string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (path.EndsWith("/info"))
            return Encoding.UTF8.GetBytes($"{Environment.MachineName}\n{HistoryStore.OldestDay():yyyy-MM-dd}");
        if (!path.EndsWith("/day")
            || !DateTime.TryParseExact(query["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || query["kind"] is not ("raw" or "min")
            || !long.TryParse(query["after"] ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out long after))
            return null;

        var text = new StringBuilder();
        using (var reader = HistoryStore.OpenDay(day, minutes: query["kind"] == "min"))
        {
            string? line;
            while (reader != null && (line = reader.ReadLine()) != null)
            {
                int a = line.IndexOf(','), b = a < 0 ? -1 : line.IndexOf(',', a + 1);
                if (b > a && long.TryParse(line.AsSpan(a + 1, b - a - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long t) && t > after)
                    text.Append(line).Append('\n');
            }
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}

/// <summary>
/// The watcher's side: the same reads the History tab makes of the local history, answered by another PC.
/// Finished days are fetched once; today is fetched incrementally (only the lines since the last fetch).
/// </summary>
public sealed class RemoteHistory
{
    public string Name { get; }
    public string Address { get; }
    public int Port { get; }
    private readonly byte[] _key;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient SlowHttp = new() { Timeout = TimeSpan.FromHours(1) }; // installs: upload, download, run

    private readonly Dictionary<(DateTime Day, bool Minutes), string> _finished = new();
    private readonly StringBuilder _today = new();
    private DateTime _todayDay;
    private long _todayAfter;
    private HashSet<string> _lastFetch = new();
    private readonly object _gate = new();

    public RemoteHistory(string name, string address, int port, byte[] key) => (Name, Address, Port, _key) = (name, address, port, key);

    /// <summary>The PC's latest full reading (what its own Resources tab shows), as JSON.</summary>
    public string Live() => Get("live");

    /// <summary>Installs a winget package there; returns its "ok"/"failed" line and details.</summary>
    public string Install(string wingetId) => Send(HttpMethod.Post, $"install?winget={Uri.EscapeDataString(wingetId)}", null, SlowHttp);

    /// <summary>Uploads an installer and runs it there with these options (an .msi always runs quietly).</summary>
    public string InstallFile(string fileName, byte[] file, string args) =>
        Send(HttpMethod.Post, $"install?file={Uri.EscapeDataString(fileName)}&args={Uri.EscapeDataString(args)}", file, SlowHttp);

    /// <summary>Asks the PC who it is — the check made before a PC is added.</summary>
    public string Hello() => Get("info").Split('\n')[0];

    public DateTime? OldestDay()
    {
        var oldest = Get("info").Split('\n').ElementAtOrDefault(1);
        return DateTime.TryParseExact(oldest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public HistoryData ReadDay(DateTime day, bool minutes)
    {
        string text;
        lock (_gate)
        {
            if (day < DateTime.Today)
            {
                if (!_finished.TryGetValue((day, minutes), out text!))
                    _finished[(day, minutes)] = text = Get($"day?date={day:yyyy-MM-dd}&kind={(minutes ? "min" : "raw")}");
            }
            else if (minutes) text = Get($"day?date={day:yyyy-MM-dd}&kind=min"); // today's summary is small
            else
            {
                if (_todayDay != day) { _today.Clear(); _todayAfter = 0; _lastFetch.Clear(); _todayDay = day; }
                // A launch event carries the moment the app appeared, ~10 s before it's written: ask from 30 s
                // before the newest line seen, and skip what the previous answer (which covered that stretch) had.
                string fresh = Get($"day?date={day:yyyy-MM-dd}&kind=raw&after={Math.Max(0, _todayAfter - 30)}");
                var fetched = new HashSet<string>();
                foreach (var line in fresh.Split('\n'))
                {
                    if (line.Length == 0 || !fetched.Add(line)) continue;
                    if (!_lastFetch.Contains(line)) _today.Append(line).Append('\n');
                    var f = line.Split(',');
                    if (f.Length > 1 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long t) && t > _todayAfter) _todayAfter = t;
                }
                _lastFetch = fetched;
                text = _today.ToString();
            }
        }
        return HistoryStore.Parse(new StringReader(text));
    }

    public HistoryData Read(DateTime from, DateTime to) => Combine(from, to, minutes: false);
    public HistoryData ReadMinutes(DateTime from, DateTime to) => Combine(from, to, minutes: true);

    private HistoryData Combine(DateTime from, DateTime to, bool minutes)
    {
        var all = new HistoryData(new(), new(), new());
        DateTime fromUtc = from.ToUniversalTime(), toUtc = to.ToUniversalTime();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var d = ReadDay(day, minutes);
            all.System.AddRange(d.System.Where(s => s.Time >= fromUtc && s.Time <= toUtc));
            all.Processes.AddRange(d.Processes.Where(p => p.Time >= fromUtc && p.Time <= toUtc));
            all.Events.AddRange(d.Events.Where(e => e.Time >= fromUtc && e.Time <= toUtc));
        }
        return all;
    }

    private string Get(string relative) => Send(HttpMethod.Get, relative, null, Http);

    private string Send(HttpMethod method, string relative, byte[]? upload, HttpClient http)
    {
        string path = HistoryServer.Prefix + relative;
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(method, $"http://{Address}:{Port}{path}");
        request.Headers.Add("X-Time", time.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Auth", RemoteCrypto.Sign(_key, path, time));
        if (upload != null) request.Content = new ByteArrayContent(RemoteCrypto.Seal(_key, $"upload\n{path}\n{time}", upload));
        HttpResponseMessage response;
        try { response = http.Send(request); }
        catch (HttpRequestException ex) { throw new RemoteHistoryException($"can't reach {Address}:{Port} — {ex.Message}"); }
        catch (TaskCanceledException) { throw new RemoteHistoryException($"{Address}:{Port} didn't answer in time"); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RemoteHistoryException("refused — the access key doesn't match, or the two PCs' clocks differ by over 2 minutes");
            if (response.StatusCode == HttpStatusCode.BadRequest && method == HttpMethod.Post)
                throw new RemoteHistoryException("refused the install — it runs a Sysoptimizer older than 1.7, or the request was malformed");
            if (!response.IsSuccessStatusCode) throw new RemoteHistoryException($"answered {(int)response.StatusCode} {response.ReasonPhrase}");
            using var body = new MemoryStream();
            response.Content.ReadAsStream().CopyTo(body);
            try { return Encoding.UTF8.GetString(RemoteCrypto.Open(_key, $"{path}\n{time}", body.ToArray())); }
            catch (CryptographicException) { throw new RemoteHistoryException("sent an answer that doesn't check out (wrong key, or altered on the way)"); }
        }
    }
}

public sealed class RemoteHistoryException(string message) : Exception(message);
