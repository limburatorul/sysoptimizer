using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Sysoptimizer.Services;

/// <summary>
/// A read-only page for a phone's browser on the home network: the PCs' cards, refreshed every 5 seconds.
/// Browsers can't sign requests over plain HTTP, so this one uses its own password (HTTP Basic, which the
/// network can see) — never the remote-control key or password — and shows readings only, no actions.
/// After 10 wrong passwords in 5 minutes it refuses every attempt for the next 5.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _hash, _salt;
    private readonly Func<byte[]> _pcs;
    private readonly Dictionary<string, DateTime> _accepted = new(); // hashes of Authorization headers that checked out, until when
    private readonly List<DateTime> _failures = new();
    private DateTime _lockedUntil;
    private readonly object _gate = new();

    /// <param name="hash">The password's key (<see cref="RemoteCrypto.FromPassword"/>) with <paramref name="salt"/>.</param>
    /// <param name="pcs">The cards, as JSON: [{ "name", "online", "status" }].</param>
    public PhoneServer(int port, byte[] hash, byte[] salt, Func<byte[]> pcs, string host = "+")
    {
        (_hash, _salt, _pcs) = (hash, salt, pcs);
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'";
            switch (Authorize(context.Request.Headers["Authorization"]))
            {
                case false:
                    response.StatusCode = 401;
                    response.Headers["WWW-Authenticate"] = "Basic realm=\"Sysoptimizer\", charset=\"UTF-8\"";
                    return;
                case null:
                    response.StatusCode = 429; // locked out for a while
                    return;
            }
            byte[] body;
            switch (context.Request.Url!.AbsolutePath)
            {
                case "/": body = Encoding.UTF8.GetBytes(Page); response.ContentType = "text/html; charset=utf-8"; break;
                case "/api/pcs": body = _pcs(); response.ContentType = "application/json"; break;
                default: response.StatusCode = 404; return;
            }
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException) { }
        finally
        {
            try { response.Close(); } catch (HttpListenerException) { }
        }
    }

    /// <summary>true = let in; false = ask for the password; null = locked out.</summary>
    private bool? Authorize(string? header)
    {
        var now = DateTime.UtcNow;
        string id = header == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header)));
        lock (_gate)
        {
            if (header != null && _accepted.TryGetValue(id, out var until) && until > now) return true;
            if (now < _lockedUntil) return null;
        }
        if (header == null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string password;
        try
        {
            string pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            password = pair[(pair.IndexOf(':') + 1)..]; // any user name
        }
        catch (FormatException) { return false; }
        bool ok = CryptographicOperations.FixedTimeEquals(RemoteCrypto.FromPassword(password, _salt), _hash); // ~half a second, once per browser
        lock (_gate)
        {
            if (ok) { _accepted[id] = now.AddHours(12); return true; }
            _failures.Add(now);
            _failures.RemoveAll(f => now - f > TimeSpan.FromMinutes(5));
            if (_failures.Count >= 10) { _lockedUntil = now.AddMinutes(5); _failures.Clear(); }
            return false;
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sysoptimizer</title>
        <style>
          :root { color-scheme: dark; }
          body { font: 15px/1.45 system-ui, "Segoe UI", sans-serif; background: #12161c; color: #e6e9ee; margin: 0; padding: 16px; }
          h1 { font-size: 18px; margin: 0 0 12px; }
          .card { background: #1b212b; border: 1px solid #2a3240; border-radius: 10px; padding: 12px 14px; margin-bottom: 10px; }
          .name { font-weight: 600; } .dot { color: #ef4444; } .on .dot { color: #22c55e; }
          .status { color: #9aa5b5; font-size: 13px; margin-top: 3px; white-space: pre-line; } .note { color: #6b7686; font-size: 12px; }
        </style></head><body>
        <h1>Sysoptimizer</h1><div id="pcs"></div><div class="note" id="note">Loading…</div>
        <script>
          const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
          async function load() {
            try {
              const pcs = await (await fetch(location.origin + "/api/pcs", { cache: "no-store" })).json(); // origin: a bookmarked user:pass@ address makes relative fetches fail
              document.getElementById("pcs").innerHTML = pcs.map(p =>
                `<div class="card ${p.online ? "on" : ""}"><div class="name"><span class="dot">●</span> ${esc(p.name)}</div><div class="status">${esc(p.status)}</div></div>`).join("");
              document.getElementById("note").textContent = "Updated " + new Date().toLocaleTimeString();
            } catch { document.getElementById("note").textContent = "Can't reach the PC — retrying…"; }
          }
          load(); setInterval(load, 5000);
        </script></body></html>
        """;
}
