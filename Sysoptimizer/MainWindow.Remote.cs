using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>
/// Remote monitoring (see RemoteHistory.cs): this PC can share its history with a key or a password, and
/// every tab can show another PC instead of this one — and act on it (install, update, end an app, clean up,
/// restart, shut down).
/// </summary>
public partial class MainWindow
{
    private const string RemoteKey = @"Software\Sysoptimizer\Remote";
    private const string RemotePcsKey = @"Software\Sysoptimizer\RemotePCs";
    private const string FirewallRule = "Sysoptimizer remote monitoring";
    private const int DefaultPort = 7777;

    private HistoryServer? _server;
    private RemoteHistory? _source; // null = this PC
    private string? _sourceError;
    private readonly List<RemoteHistory> _remotes = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PcCard> _pcCards = new();

    private void InitRemote()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RemoteKey))
        {
            RemotePortBox.Text = (key?.GetValue("Port") is int port ? port : DefaultPort).ToString();
            RemoteKeyBox.Text = key?.GetValue("Key") as string ?? "";
            RemoteServerCheck.IsChecked = key?.GetValue("ServerOn") is 1;
        }
        if (RemoteCrypto.ParseKey(RemoteKeyBox.Text) == null) // made once, so it's there to copy before sharing is on
        {
            RemoteKeyBox.Text = RemoteCrypto.NewKey();
            SaveServerSettings(on: RemoteServerCheck.IsChecked == true, int.TryParse(RemotePortBox.Text, out int p) ? p : DefaultPort);
        }
        if (RemoteServerCheck.IsChecked == true) StartServer();
        else RemoteServerStatus.Text = "Off.";

        using (var key = Registry.CurrentUser.OpenSubKey(RemotePcsKey))
            foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
                if (key!.GetValue(name) is string data && data.Split('|') is [var address, var portText, var keyText, .. var rest]
                    && int.TryParse(portText, out int port) && RemoteCrypto.ParseKey(keyText) is { } secret)
                    _remotes.Add(new RemoteHistory(name, address, port, secret)
                    {
                        Macs = rest.FirstOrDefault()?.Split(',').Where(WakeOnLan.IsMac).ToArray() ?? Array.Empty<string>(),
                    });
        WatchedPcsList.ItemsSource = _pcCards;
        // The PC picked last time stays picked: watching another PC is usually what this one is set up for.
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            RefreshRemoteLists(key?.GetValue("Showing") as string);
        Closed += (_, _) => _server?.Dispose();
        var pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        pollTimer.Tick += (_, _) => PollRemotes();
        pollTimer.Start();
    }

    // --- sharing this PC ---

    private void RemoteServer_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteServerCheck.IsChecked == true) StartServer();
        else StopServer();
    }

    private void StartServer()
    {
        StopServer(removeRule: false);
        if (!int.TryParse(RemotePortBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            RemoteServerCheck.IsChecked = false;
            RemoteServerStatus.Text = "The port must be a whole number from 1024 to 65535.";
            return;
        }
        if (RemoteCrypto.ParseKey(RemoteKeyBox.Text) == null) RemoteKeyBox.Text = RemoteCrypto.NewKey();
        try
        {
            _server = new HistoryServer(port, RemoteCrypto.ParseKey(RemoteKeyBox.Text)!, Salt(), () => _liveJson,
                () => JsonSerializer.SerializeToUtf8Bytes(_lastProcesses.Select(p => p with { Path = null }), LiveJson), // a path means nothing on the watching PC
                EnergyLines, command => RunCommand(command, scheduled: false), RemoteList);
        }
        catch (HttpListenerException ex)
        {
            RemoteServerCheck.IsChecked = false;
            RemoteServerStatus.Text = $"Couldn't listen on port {port} — {ex.Message}";
            return;
        }
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={port} profile=private");
        Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=UDP localport={port} profile=private"); // "who's sharing?"
        SaveServerSettings(on: true, port);

        var addresses = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString());
        RemoteServerStatus.Text = $"Sharing. On the other PC, add {Environment.MachineName} (or {string.Join(", ", addresses)}), port {port}, and this key or your password. "
            + "If Windows calls this network Public, the firewall keeps it closed — set the network to Private.";
        Log($"Sharing this PC's history on port {port}.");
    }

    private void StopServer(bool removeRule = true)
    {
        if (_server == null && !removeRule) return;
        _server?.Dispose();
        _server = null;
        if (!removeRule) return;
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        SaveServerSettings(on: false, int.TryParse(RemotePortBox.Text, out int port) ? port : DefaultPort);
        RemoteServerStatus.Text = "Off.";
    }

    private void SaveServerSettings(bool on, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RemoteKey);
        key.SetValue("ServerOn", on ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Port", port, RegistryValueKind.DWord);
        if (RemoteKeyBox.Text.Length > 0) key.SetValue("Key", RemoteKeyBox.Text);
    }

    /// <summary>This PC's password salt, made once: the same password gives a different key here than anywhere else.</summary>
    private static byte[] Salt()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RemoteKey);
        if (key.GetValue("Salt") is string text && RemoteCrypto.ParseSalt(text) is { } salt) return salt;
        salt = RemoteCrypto.NewSalt();
        key.SetValue("Salt", RemoteCrypto.Base64Url(salt));
        return salt;
    }

    private void RemoteNewKey_Click(object sender, RoutedEventArgs e) => UseKey(RemoteCrypto.NewKey(), "New access key made");

    private async void RemoteUsePassword_Click(object sender, RoutedEventArgs e)
    {
        string password = RemotePasswordBox.Password;
        if (password.Length < RemoteCrypto.MinPasswordLength)
        {
            RemoteServerStatus.Text = $"The password needs at least {RemoteCrypto.MinPasswordLength} characters — it guards installs and restarts too.";
            return;
        }
        RemotePasswordBox.Clear();
        var salt = Salt();
        var key = await Task.Run(() => RemoteCrypto.FromPassword(password, salt)); // ~half a second, on purpose
        UseKey(RemoteCrypto.Base64Url(key), "Password set — on the other PC, type it where the key goes");
    }

    private void UseKey(string key, string what)
    {
        RemoteKeyBox.Text = key;
        if (_server != null) StartServer(); // the old key stops working now
        else SaveServerSettings(on: false, int.TryParse(RemotePortBox.Text, out int port) ? port : DefaultPort);
        Log($"{what}. PCs using the old key or password can no longer reach this one.");
    }

    private void RemoteCopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteKeyBox.Text.Length == 0) RemoteKeyBox.Text = RemoteCrypto.NewKey();
        Clipboard.SetText(RemoteKeyBox.Text);
        Log("Access key copied.");
    }

    private static void Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(10000);
    }

    // --- what a watching PC asks of this one ---

    /// <summary>Windows' energy figures per app, for /energy: "app\tWh" lines, or one "!reason" line.</summary>
    private static string EnergyLines(DateTime fromUtc, DateTime toUtc)
    {
        try { return string.Join("\n", EnergyService.Top(fromUtc, toUtc).Select(t => $"{t.App}\t{t.Wh.ToString("0.###", CultureInfo.InvariantCulture)}")); }
        catch (IOException ex) { return "!" + ex.Message; }
    }

    /// <summary>/startup, /services and /boots: the lists the Optimize tab shows, as this PC has them.</summary>
    private static byte[]? RemoteList(string name) => name switch
    {
        "startup" => JsonSerializer.SerializeToUtf8Bytes(StartupService.GetItems()),
        "services" => JsonSerializer.SerializeToUtf8Bytes(ServiceManager.KnownServices.ToDictionary(s => s.Key, s => ServiceManager.GetStatus(s.Key))),
        "boots" => JsonSerializer.SerializeToUtf8Bytes(BootService.Read()),
        _ => null,
    };

    /// <summary>
    /// Runs on a background thread — asked by a watching PC, or due in the schedule: announced here first
    /// (tray, log, Events), then done and reported back.
    /// </summary>
    private string RunCommand(RemoteCommand command, bool scheduled)
    {
        string what = command.Action switch
        {
            "update-app" => "updating Sysoptimizer",
            "startup-enable" => $"letting {command.Target} start with Windows",
            "startup-disable" => $"stopping {command.Target} from starting with Windows",
            "service-enable" => $"re-enabling the {command.Target} service",
            "service-disable" => $"stopping and disabling the {command.Target} service",
            "install-winget" or "install-file" => $"installing {command.Target}",
            "install-url" => $"installing from {command.Target}",
            "upgrade-all" => "updating every app",
            "kill" => $"ending {command.Target}",
            "cleanup" => "cleaning up temporary files",
            "restart" => "restarting this PC in 1 minute",
            _ => "shutting this PC down in 1 minute",
        };
        Dispatcher.Invoke(() => Alert($"remote:{Guid.NewGuid()}", scheduled ? $"Scheduled: {what}" : $"A watching PC is {what} here"));
        string output;
        bool ok = command.Action switch
        {
            "install-winget" => WingetService.Install(command.Target!, out output),
            "install-file" => RunInstaller(command.Target!, folder => File.WriteAllBytes(Path.Combine(folder, command.Target!), command.File!), command.Args, out output),
            "install-url" => InstallFromLink(command.Target!, command.Args, out output),
            "upgrade-all" => WingetService.UpgradeAll(out output),
            "kill" => EndApp(command.Target!, out output),
            "cleanup" => CleanAll(out output),
            "update-app" => UpdateSelf(out output),
            "startup-enable" or "startup-disable" => SetStartup(command.Target!, enable: command.Action == "startup-enable", out output),
            "service-enable" or "service-disable" => SetService(command.Target!, enable: command.Action == "service-enable", out output),
            _ => ShutDown(restart: command.Action == "restart", out output),
        };
        Dispatcher.Invoke(() => Log($"{(scheduled ? "Scheduled" : "Remote request")} ({what}): {(ok ? "done" : "failed")} — {LastLine(output)}"));
        return $"{(ok ? "ok" : "failed")}\n{output}";
    }

    /// <summary>Puts the installer in a fresh folder (via <paramref name="fetch"/>), runs it with its options, removes it.</summary>
    private static bool RunInstaller(string fileName, Action<string> fetch, string args, out string output)
    {
        // The elevated user's own temp folder: other accounts on this PC can't swap the file before it runs.
        string folder = Path.Combine(Path.GetTempPath(), "Sysoptimizer-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            fetch(folder);
            string file = Path.Combine(folder, fileName);
            // No hidden window: an installer without quiet options shows up for whoever sits at this PC.
            var psi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{file}\" /qn /norestart {args}")
                : new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            using var process = Process.Start(psi)!;
            if (!process.WaitForExit(TimeSpan.FromMinutes(30)))
            {
                output = "still running after 30 minutes — it may be waiting for someone to click through it on that PC";
                return false;
            }
            output = process.ExitCode switch
            {
                0 => "installed",
                3010 or 1641 => "installed — Windows needs a restart to finish",
                var code => $"the installer ended with code {code}",
            };
            return process.ExitCode is 0 or 3010 or 1641;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or HttpRequestException or TaskCanceledException)
        {
            output = ex.Message;
            return false;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still running after the timeout
        }
    }

    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Downloads an installer over HTTPS (redirects followed, still HTTPS only) and runs it.</summary>
    private static bool InstallFromLink(string url, string args, out string output)
    {
        HttpResponseMessage response;
        try { response = Downloads.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { output = $"couldn't download it — {ex.Message}"; return false; }
        using (response)
        {
            var final = response.RequestMessage?.RequestUri;
            string? name = RemoteCommand.SafeFileName(response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                                                      ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                                                      ?? Path.GetFileName(final?.LocalPath ?? ""));
            if (!response.IsSuccessStatusCode) { output = $"the link answered {(int)response.StatusCode} {response.ReasonPhrase}"; return false; }
            if (final?.Scheme != Uri.UriSchemeHttps) { output = "the link redirected away from HTTPS — not downloaded"; return false; }
            if (name == null) { output = "the link doesn't lead to an .exe or .msi file"; return false; }
            return RunInstaller(name, folder =>
            {
                using var target = File.Create(Path.Combine(folder, name));
                response.Content.ReadAsStream().CopyTo(target);
            }, args, out output);
        }
    }

    private static bool EndApp(string name, out string output)
    {
        if (CriticalProcesses.Contains(name)) { output = $"{name} wasn't ended — Windows needs it running"; return false; }
        int ended = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
            {
                try { p.Kill(); ended++; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { failed++; } // protected, or already gone
            }
        output = ended + failed == 0 ? $"{name} isn't running" : $"{ended} ended" + (failed > 0 ? $", {failed} refused (protected)" : "");
        return ended > 0;
    }

    /// <summary>The newest release from GitHub, checked like the updater checks it; the app restarts into it.</summary>
    private bool UpdateSelf(out string output)
    {
        var latest = Updater.Latest().GetAwaiter().GetResult();
        if (latest == null) { output = "couldn't reach GitHub for the latest version"; return false; }
        if (latest.Version <= Updater.Current) { output = $"already up to date ({Updater.Current})"; return true; }
        string installer;
        try { installer = Updater.Download(latest, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException) { output = $"the download failed — {ex.Message}"; return false; }
        Updater.InstallAndRestart(installer);
        // Leave a few seconds for the answer to get out, then make way for the installer.
        Dispatcher.InvokeAsync(async () => { await Task.Delay(3000); ExitApp(); });
        output = $"updating {Updater.Current} → {latest.Version}; it restarts in about a minute";
        return true;
    }

    private static bool SetStartup(string name, bool enable, out string output)
    {
        var item = StartupService.GetItems().FirstOrDefault(i => i.Name == name && (i.Location == "Disabled") == enable);
        if (item == null) { output = $"{name} isn't {(enable ? "a disabled" : "an enabled")} startup entry there"; return false; }
        if (enable) StartupService.Enable(item); else StartupService.Disable(item);
        output = enable ? $"{name} starts with Windows again" : $"{name} no longer starts with Windows";
        return true;
    }

    private static bool SetService(string key, bool enable, out string output)
    {
        if (!ServiceManager.KnownServices.Any(s => s.Key == key)) { output = $"{key} isn't one of the services Sysoptimizer manages"; return false; }
        if (enable) ServiceManager.EnableAndStart(key); else ServiceManager.StopAndDisable(key);
        output = $"{key}: {ServiceManager.GetStatus(key)}";
        return true;
    }

    private static bool CleanAll(out string output)
    {
        long freed = 0;
        foreach (var target in CleanupService.Targets) freed += CleanupService.Clean(target.Path()).freed;
        output = $"{CleanupService.FormatSize(freed)} freed";
        return true;
    }

    /// <summary>A minute's notice, so whoever is at that PC can save (or cancel with shutdown /a).</summary>
    private static bool ShutDown(bool restart, out string output)
    {
        var psi = new ProcessStartInfo("shutdown.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in new[] { restart ? "/r" : "/s", "/t", "60", "/c", $"Sysoptimizer: a watching PC asked to {(restart ? "restart" : "shut down")} this PC." })
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        output = p.ExitCode == 0 ? $"{(restart ? "restarting" : "shutting down")} in 1 minute" : $"Windows refused (code {p.ExitCode}) — a restart may already be pending";
        return p.ExitCode == 0;
    }

    private static string LastLine(string text) =>
        text.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no details";

    // --- acting on another PC ---

    private async Task RunOn(RemoteHistory target, string what, Func<RemoteHistory, string> send)
    {
        Log($"{what} on {target.Name}…");
        try
        {
            string answer = await Task.Run(() => send(target));
            int split = answer.IndexOf('\n');
            string detail = LastLine(split < 0 ? "" : answer[(split + 1)..]);
            Log($"{what} on {target.Name}: {(answer.StartsWith("ok") ? detail : "failed — " + detail)}");
        }
        catch (RemoteHistoryException ex) { Log($"{what} on {target.Name}: {ex.Message}."); }
    }

    private async void InstallRemoteFile_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } target) return;
        var dialog = new OpenFileDialog { Filter = "Installers (*.exe, *.msi)|*.exe;*.msi", Title = $"Install on {target.Name}" };
        if (dialog.ShowDialog(this) != true) return;
        string name = Path.GetFileName(dialog.FileName);
        if (RemoteCommand.SafeFileName(name) == null) { Log($"{name}: only .exe and .msi files with names up to 128 characters can be sent."); return; }
        if (new FileInfo(dialog.FileName).Length > RemoteCommand.MaxFileBytes) { Log($"{name} is over 512 MB — too big to send; use Install from link instead."); return; }
        string args = InstallArgsBox.Text.Trim();
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"{name}: couldn't read it — {ex.Message}"); return; }
        await RunOn(target, $"Installing {name}", t => t.Run("install-file", name, args, bytes));
    }

    private async void InstallRemoteLink_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } target) return;
        string url = InstallLinkBox.Text.Trim();
        if (!RemoteCommand.IsHttpsLink(url)) { Log("Paste an https:// link to an .exe or .msi installer."); return; }
        string args = InstallArgsBox.Text.Trim();
        await RunOn(target, $"Installing from {url}", t => t.Run("install-url", url, args));
    }

    /// <summary>The actions on a watched PC's card (Resources tab): update apps, clean up, restart, shut down.</summary>
    private void PcActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PcCard card } button || _remotes.FirstOrDefault(r => r.Name == card.Name) is not { } target) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, string action, string? confirm = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) =>
            {
                if (confirm != null && MessageBox.Show(this, confirm, "Sysoptimizer", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                await RunOn(target, header.TrimEnd('…'), t => t.Run(action));
            };
            menu.Items.Add(item);
        }
        Add("Update all apps", "upgrade-all");
        Add("Clean up temporary files", "cleanup");
        Add("Update Sysoptimizer", "update-app");
        menu.Items.Add(new Separator());
        var wake = new MenuItem { Header = "Wake up", IsEnabled = target.Macs.Length > 0,
            ToolTip = target.Macs.Length > 0 ? "Sends a Wake-on-LAN packet (it must be allowed in that PC's BIOS)" : "Its network address is learned once it answers with Sysoptimizer 1.9 or newer" };
        wake.Click += async (_, _) => await Wake(target);
        menu.Items.Add(wake);
        Add("Restart…", "restart", $"Restart {target.Name}? Whoever is using it gets a minute's notice to save their work.");
        Add("Shut down…", "shutdown", $"Shut {target.Name} down? Whoever is using it gets a minute's notice — and you can't switch it back on from here.");
        menu.IsOpen = true;
    }

    private void PcShow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PcCard card })
            HistorySourceCombo.SelectedItem = HistorySourceCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as RemoteHistory)?.Name == card.Name);
    }

    // --- watching another PC ---

    private async void RemoteFind_Click(object sender, RoutedEventArgs e)
    {
        int port = int.TryParse(RemoteAddPortBox.Text.Trim(), out int p) && p is > 0 and <= 65535 ? p : DefaultPort;
        RemoteFindButton.IsEnabled = false;
        RemoteAddStatus.Text = $"Looking for PCs sharing on port {port}…";
        try
        {
            var found = await RemoteDiscovery.Find(port, TimeSpan.FromSeconds(2));
            RemoteFoundList.ItemsSource = found.Select(f => new OptionItem { Name = f.Name, Description = f.Address, Tag = port }).ToList();
            RemoteAddStatus.Text = found.Count == 0
                ? "None answered. Sharing must be on there (About → Share this PC), and the network set to Private on both PCs."
                : "Pick one, then type its key or password.";
        }
        catch (SocketException ex) { RemoteAddStatus.Text = $"Couldn't search the network — {ex.Message}"; }
        finally { RemoteFindButton.IsEnabled = true; }
    }

    private void RemoteFound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OptionItem found }) return;
        RemoteAddressBox.Text = found.Description;
        RemoteAddPortBox.Text = found.Tag?.ToString();
        RemoteAddKeyBox.Focus();
    }

    private async void RemoteAdd_Click(object sender, RoutedEventArgs e)
    {
        string address = RemoteAddressBox.Text.Trim();
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown) { RemoteAddStatus.Text = "Type the PC's name or IP address."; return; }
        if (!int.TryParse(RemoteAddPortBox.Text.Trim(), out int port) || port < 1 || port > 65535) { RemoteAddStatus.Text = "The port must be a number from 1 to 65535."; return; }
        string secretText = RemoteAddKeyBox.Text.Trim();
        var secret = RemoteCrypto.ParseKey(secretText);
        if (secret == null && secretText.Length < RemoteCrypto.MinPasswordLength)
        {
            RemoteAddStatus.Text = $"That's neither an access key nor a password (at least {RemoteCrypto.MinPasswordLength} characters) — copy the key from the About tab on the other PC, or type its password.";
            return;
        }

        RemoteAddButton.IsEnabled = false;
        RemoteAddStatus.Text = $"Connecting to {address}…";
        try
        {
            // A password becomes that PC's key with its own salt, which it hands out for exactly this.
            secret ??= await Task.Run(() => RemoteHistory.SaltOf(address, port) is { } salt
                ? RemoteCrypto.FromPassword(secretText, salt)
                : throw new RemoteHistoryException("has no password set (or runs an older Sysoptimizer) — use its access key"));
            string name = await Task.Run(() => new RemoteHistory(address, address, port, secret).Hello());
            name = name.Replace('|', '_');
            _remotes.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            _remotes.Add(new RemoteHistory(name, address, port, secret));
            using (var key = Registry.CurrentUser.CreateSubKey(RemotePcsKey))
                key.SetValue(name, _remotes[^1].Saved);
            RemoteAddKeyBox.Clear();
            RemoteFoundList.ItemsSource = null;
            RemoteAddStatus.Text = $"Added {name}. Pick it under Showing, in the bar at the bottom, or on its card in Resources.";
            RefreshRemoteLists();
        }
        catch (RemoteHistoryException ex) { RemoteAddStatus.Text = $"{address}: {ex.Message}."; }
        finally { RemoteAddButton.IsEnabled = true; }
    }

    private void RemoteRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        _remotes.RemoveAll(r => r.Name == name);
        using (var key = Registry.CurrentUser.CreateSubKey(RemotePcsKey)) key.DeleteValue(name, throwOnMissingValue: false);
        if (_source?.Name == name) HistorySourceCombo.SelectedIndex = 0;
        RefreshRemoteLists();
    }

    private void RefreshRemoteLists(string? select = null)
    {
        RemotePcList.ItemsSource = _remotes.Select(r => new OptionItem { Name = r.Name, Description = $"{r.Address}, port {r.Port}", IsChecked = RemoteAlertsOn(r.Name) }).ToList();
        foreach (var gone in _pcCards.Where(c => !_remotes.Any(r => r.Name == c.Name)).ToList()) _pcCards.Remove(gone);
        foreach (var r in _remotes.Where(r => !_pcCards.Any(c => c.Name == r.Name))) _pcCards.Add(new PcCard { Name = r.Name });
        WatchedPcsPanel.Visibility = _pcCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) RefreshSchedule();
        string? selected = select ?? _source?.Name;
        HistorySourceCombo.Items.Clear();
        HistorySourceCombo.Items.Add(new ComboBoxItem { Content = "This PC" });
        foreach (var r in _remotes) HistorySourceCombo.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r });
        HistorySourceCombo.SelectedItem = HistorySourceCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((i.Tag as RemoteHistory)?.Name, selected, StringComparison.OrdinalIgnoreCase))
                                          ?? HistorySourceCombo.Items[0];
    }

    /// <summary>Every tab now shows another PC (or this one again): start from a clean slate.</summary>
    private void HistorySource_Changed(object sender, SelectionChangedEventArgs e)
    {
        var source = (HistorySourceCombo.SelectedItem as ComboBoxItem)?.Tag as RemoteHistory;
        if (source == _source) return;
        _source = source;
        if (HistorySourceCombo.SelectedItem != null) // not the moment the list is being refilled
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Sysoptimizer"))
                key.SetValue("Showing", source?.Name ?? "");
        _sourceError = null;
        _dayCache.Clear();
        _dayLoading.Clear();
        _navData = new();
        _navLoadedAt = DateTime.MinValue;
        RetentionCombo.IsEnabled = source == null; // retention is this PC's own setting
        Title = source == null ? "Sysoptimizer" : $"Sysoptimizer — watching {source.Name}";
        // What acts on this PC alone (health, freeing memory, blocking apps) has no place while another one is shown.
        HealthCardBorder.Visibility = FreeMemoryButton.Visibility = UpgradeAllButton.Visibility = BlockAppsButton.Visibility
            = source == null ? Visibility.Visible : Visibility.Collapsed;
        if (source != null) UnblockButton.Visibility = BlockedPanel.Visibility = Visibility.Collapsed;
        else RefreshBlocked();
        // Installing and the app list follow the PC being shown.
        RemoteInstallPanel.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        InstallSelectedButton.Content = source == null ? "Install selected" : $"Install selected on {source.Name}";
        foreach (var card in _pcCards) card.Shown = card.Name == source?.Name;
        _remoteProcesses = new();
        _processRows.Clear();
        _processRowMap.Clear();
        ResetLiveGraphs();
        if (_historyApp != null) HistoryAppClear_Click(this, new RoutedEventArgs());
        if (!IsLoaded) return;
        _animateChart = true;
        _ = LoadHistory();
        if (EventsList.IsVisible) _ = LoadEvents();
        if (ProcessesList.IsVisible && source == null) UpdateProcessRows();
        if (StartupList.IsVisible) _ = LoadStartup();
        if (ServicesList.IsVisible) _ = LoadServices();
    }
}

/// <summary>A watched PC's card on the Resources tab, refreshed every 5 seconds.</summary>
public sealed class PcCard : INotifyPropertyChanged
{
    public required string Name { get; init; }
    private string _status = "Connecting…";
    private bool _online, _shown;
    private string _version = "";
    public string Version { get => _version; set { _version = value; Raise(nameof(Version)); } }
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }
    public bool Online { get => _online; set { _online = value; Raise(nameof(Online)); } }
    public bool Shown { get => _shown; set { _shown = value; Raise(nameof(Shown)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
