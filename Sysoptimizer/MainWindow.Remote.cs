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
/// send it files, restart, shut down), one PC at a time or all of them at once.
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
                EnergyLines, command => RunCommand(command, scheduled: false), RemoteList, ShareFile);
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

    /// <summary>/startup, /services and /boots: the lists the Optimize tab shows, as this PC has them; /log: the end of its log file.</summary>
    private static byte[]? RemoteList(string name) => name switch
    {
        "startup" => JsonSerializer.SerializeToUtf8Bytes(StartupService.GetItems()),
        "services" => JsonSerializer.SerializeToUtf8Bytes(ServiceManager.KnownServices.ToDictionary(s => s.Key, s => ServiceManager.GetStatus(s.Key))),
        "boots" => JsonSerializer.SerializeToUtf8Bytes(BootService.Read()),
        "log" => System.Text.Encoding.UTF8.GetBytes(AppLog.Tail()),
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
            "receive-file" => $"sending {command.Target} to the Downloads folder",
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
            "receive-file" => SaveReceived(command.Target!, command.File!, out output),
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

    /// <summary>A watching PC lists a folder (name null) or copies a file from it — announced here, like a command.</summary>
    private byte[]? ShareFile(string? folder, string? path, string? name)
    {
        if (name == null) return RemoteFiles.List(folder, path);
        if (RemoteFiles.FileOf(folder, name) is not { } file) return null;
        Dispatcher.Invoke(() => Alert($"remote:{Guid.NewGuid()}", $"A watching PC is copying {name} from {folder}"));
        try { return File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // in use, or gone meanwhile
    }

    /// <summary>
    /// A file sent from (or copied off) another PC, saved in Downloads under its own name — or "name (2).ext" beside one
    /// already there, never over it. Marked as from the internet, as a browser would, so Windows asks before
    /// running a program that arrived this way.
    /// </summary>
    private static bool SaveReceived(string name, byte[] bytes, out string output)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        try
        {
            Directory.CreateDirectory(folder);
            for (int n = 1; ; n++)
            {
                string path = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){ext}");
                FileStream file;
                try { file = new FileStream(path, FileMode.CreateNew); }
                catch (IOException) when (File.Exists(path) && n < 1000) { continue; }
                using (file) file.Write(bytes);
                File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                output = $"saved in Downloads as {Path.GetFileName(path)}";
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output = $"couldn't save it — {ex.Message}"; return false; }
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
        Updater.Release? latest;
        try { latest = Updater.Latest().GetAwaiter().GetResult(); }
        catch (Exception ex) when (Updater.Failed(ex)) { output = "couldn't reach GitHub for the latest version"; return false; }
        if (latest == null) { output = "the latest release has nothing for this copy to install"; return false; }
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

    /// <summary>
    /// The actions on a watched PC's card (Resources tab) — or, from "All PCs", on every watched PC at once:
    /// update apps, clean up, send files, wake, restart, shut down.
    /// </summary>
    private void PcActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var targets = button.Tag is PcCard card ? _remotes.Where(r => r.Name == card.Name).ToList() : _remotes.ToList();
        if (targets.Count == 0) return;
        string who = targets.Count == 1 ? targets[0].Name : $"all {targets.Count} watched PCs";
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Group(string header)
        {
            var group = new MenuItem { Header = header };
            menu.Items.Add(group);
            return group;
        }
        MenuItem Item(MenuItem group, string header, Action click, string? tip = null)
        {
            var item = new MenuItem { Header = header, ToolTip = tip };
            item.Click += (_, _) => click();
            group.Items.Add(item);
            return item;
        }
        void Command(MenuItem group, string header, string action, string? confirm = null) => Item(group, header, async () =>
        {
            if (confirm != null && MessageBox.Show(this, confirm, "Sysoptimizer", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            await Task.WhenAll(targets.Select(target => RunOn(target, header.TrimEnd('…'), t => t.Run(action))));
        });

        var apps = Group("Apps");
        Command(apps, "Update all apps", "upgrade-all");
        Command(apps, "Update Sysoptimizer", "update-app");
        var files = Group("Files");
        Item(files, "Send files…", async () =>
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = $"Send to {who}" };
            if (dialog.ShowDialog(this) == true) await SendFiles(targets, dialog.FileNames);
        }, "Saved in its Downloads folder. You can also drop files on a PC's line.");
        if (targets.Count == 1) Item(files, "Get files…", () => GetFiles(targets[0]), "Copy files from its Downloads, Desktop or Documents to your Downloads");
        var maintenance = Group("Maintenance");
        Command(maintenance, "Clean up temporary files", "cleanup");
        if (targets.Count == 1) Item(maintenance, "Its log…", () => ShowLog(targets[0]), "What Sysoptimizer did on that PC, and any error or crash — from its log file");
        menu.Items.Add(new Separator());
        var power = Group("Power");
        bool wakeable = targets.Any(t => t.Macs.Length > 0);
        Item(power, "Wake up", async () => await Task.WhenAll(targets.Select(Wake)),
            wakeable ? "Sends a Wake-on-LAN packet (it must be allowed in that PC's BIOS)" : "A PC's network address is learned once it answers with Sysoptimizer 1.9 or newer").IsEnabled = wakeable;
        Command(power, "Restart…", "restart", $"Restart {who}? Whoever is using it gets a minute's notice to save their work.");
        Command(power, "Shut down…", "shutdown", $"Shut {who} down? Whoever is using it gets a minute's notice — and you can't switch it back on from here.");
        menu.IsOpen = true;
    }

    /// <summary>A window in the app's theme: its colors, its title bar, the acrylic backdrop.</summary>
    private Window ThemedWindow(string title, UIElement content, double width)
    {
        var card = new Border { Padding = new Thickness(16), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        var window = new Window
        {
            Title = title, Owner = this, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.Transparent, Content = card,
        };
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        window.SourceInitialized += (_, _) => WindowGlass.EnableAcrylic(window);
        return window;
    }

    // --- transfers: every file on its way, with what it's doing, how far, how fast and how long is left ---

    private Window? _transfersWindow;
    private StackPanel? _transferRows;

    private TransferRow NewTransfer(string title)
    {
        if (_transfersWindow == null)
        {
            _transferRows = new StackPanel();
            var scroll = new ScrollViewer { Content = _transferRows, MaxHeight = 520 };
            scroll.SetResourceReference(StyleProperty, "GlassScrollViewer");
            _transfersWindow = ThemedWindow("Transfers", scroll, 560);
            _transfersWindow.Closed += (_, _) => (_transfersWindow, _transferRows) = (null, null);
            _transfersWindow.Show();
        }
        var row = new TransferRow(title, Dispatcher);
        _transferRows!.Children.Insert(0, row.View); // newest on top
        return row;
    }

    /// <summary>
    /// A window over the watched PC's Downloads, Desktop or Documents and the folders in them: open folders,
    /// filter by name, pick files, and they're copied to this PC's Downloads (saved like a received file:
    /// never over one, marked as from another computer), each followed in Transfers.
    /// </summary>
    private void GetFiles(RemoteHistory target)
    {
        string sub = ""; // the folder open inside the chosen one, "a/b"
        var entries = new List<RemoteFiles.Entry>();
        var folder = new ComboBox { ItemsSource = RemoteFiles.Folders, SelectedIndex = 0, Width = 150 };
        System.Windows.Automation.AutomationProperties.SetName(folder, "Folder");
        var up = new Button { Content = "\uE74A", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "Up one folder" };
        System.Windows.Automation.AutomationProperties.SetName(up, "Up one folder");
        var search = new TextBox { Width = 200, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Show only names containing this" };
        System.Windows.Automation.AutomationProperties.SetName(search, "Filter by name");
        var where = new TextBlock { Margin = new Thickness(0, 8, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        where.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var list = new ListBox { SelectionMode = SelectionMode.Extended, Height = 380, Background = System.Windows.Media.Brushes.Transparent };
        list.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        list.SetResourceReference(Control.BorderBrushProperty, "Border");
        System.Windows.Automation.AutomationProperties.SetName(list, "Files and folders");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var copy = new Button { Content = "Copy to my Downloads", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
        copy.SetResourceReference(StyleProperty, "PrimaryButton");

        var bar = new DockPanel();
        DockPanel.SetDock(search, Dock.Right);
        bar.Children.Add(search);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(folder);
        left.Children.Add(up);
        bar.Children.Add(left);
        var panel = new DockPanel();
        foreach (var (element, dock) in new (UIElement, Dock)[] { (bar, Dock.Top), (where, Dock.Top), (copy, Dock.Bottom), (status, Dock.Bottom) })
        {
            DockPanel.SetDock(element, dock);
            panel.Children.Add(element);
        }
        panel.Children.Add(list);
        var window = ThemedWindow($"Files on {target.Name}", panel, 640);

        string FolderName() => (string)folder.SelectedItem;
        string PathOf(string name) => sub.Length == 0 ? name : $"{sub}/{name}";

        void Fill()
        {
            list.Items.Clear();
            string filter = search.Text.Trim();
            var shown = entries.Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
            foreach (var e in shown)
            {
                var icon = new TextBlock { Text = e.Folder ? "\uE8B7" : "\uE8A5", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var detail = new TextBlock { Text = e.Folder ? "  folder" : $"  {CleanupService.FormatSize(e.Bytes)} · {e.Modified.ToLocalTime():d MMM yyyy HH:mm}", Opacity = 0.65 };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(icon);
                line.Children.Add(new TextBlock { Text = e.Name });
                line.Children.Add(detail);
                var item = new ListBoxItem { Content = line, Tag = e };
                System.Windows.Automation.AutomationProperties.SetName(item, e.Folder ? $"Folder {e.Name}" : e.Name);
                list.Items.Add(item);
            }
            int files = shown.Count(e => !e.Folder), folders = shown.Count - files;
            status.Text = shown.Count == 0 ? (entries.Count == 0 ? "Nothing here." : "Nothing matches the filter.")
                : $"{folders} folders, {files} files. Open a folder with a double-click or Enter; pick files with Ctrl or Shift.";
        }

        async Task Load()
        {
            where.Text = $"{target.Name} › {FolderName()}" + (sub.Length > 0 ? " › " + sub.Replace("/", " › ") : "");
            up.IsEnabled = sub.Length > 0;
            status.Text = "Reading…";
            list.Items.Clear();
            try { entries = await Task.Run(() => target.Files(FolderName(), sub)); }
            catch (RemoteHistoryException ex) { entries = new(); status.Text = $"Can't read it — {ex.Message}"; return; }
            Fill();
        }

        void Open()
        {
            if (list.SelectedItem is ListBoxItem { Tag: RemoteFiles.Entry { Folder: true } e }) { sub = PathOf(e.Name); search.Clear(); _ = Load(); }
        }

        folder.SelectionChanged += async (_, _) => { sub = ""; await Load(); };
        up.Click += async (_, _) => { sub = sub.Contains('/') ? sub[..sub.LastIndexOf('/')] : ""; search.Clear(); await Load(); };
        search.TextChanged += (_, _) => Fill();
        list.MouseDoubleClick += (_, _) => Open();
        list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Open(); e.Handled = true; } };
        list.SelectionChanged += (_, _) => copy.IsEnabled = list.SelectedItems.Cast<ListBoxItem>().Any(i => i.Tag is RemoteFiles.Entry { Folder: false });
        copy.Click += async (_, _) =>
        {
            string from = FolderName();
            var picked = list.SelectedItems.Cast<ListBoxItem>().Select(i => (RemoteFiles.Entry)i.Tag).Where(f => !f.Folder).Select(f => (f.Name, Path: PathOf(f.Name))).ToList();
            foreach (var (name, path) in picked)
            {
                var row = NewTransfer($"{name}  ←  {target.Name}");
                row.Phase($"Asking {target.Name} for it…");
                try
                {
                    var bytes = await Task.Run(() => target.CopyFile(from, path, (done, total) => row.Progress(done, total, "Receiving")));
                    row.Phase("Saving…");
                    var (ok, output) = await Task.Run(() => (SaveReceived(name, bytes, out var o), o));
                    row.Done(ok ? $"Done — {output}" : $"Failed — couldn't save it: {output}", ok);
                    Log($"{name} from {target.Name}: {output}");
                }
                catch (RemoteHistoryException ex) { row.Done($"Failed — {ex.Message}", false); Log($"{name} from {target.Name}: {ex.Message}"); }
            }
        };
        window.Show();
        _ = Load();
    }

    /// <summary>The end of a watched PC's log file, newest at the bottom, with a Refresh.</summary>
    private void ShowLog(RemoteHistory target)
    {
        var text = new TextBox
        {
            IsReadOnly = true, Height = 460, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
        };
        System.Windows.Automation.AutomationProperties.SetName(text, $"{target.Name}'s log");
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Bottom);
        panel.Children.Add(refresh);
        panel.Children.Add(text);
        var window = ThemedWindow($"Log of {target.Name}", panel, 900);
        async Task Load()
        {
            refresh.IsEnabled = false;
            try
            {
                string log = await Task.Run(() => target.List("log"));
                text.Text = log.Length == 0 ? "The log is empty." : log;
            }
            catch (RemoteHistoryException ex) { text.Text = $"Can't read it — {ex.Message}"; }
            text.ScrollToEnd();
            refresh.IsEnabled = true;
        }
        refresh.Click += async (_, _) => await Load();
        window.Show();
        _ = Load();
    }

    /// <summary>Files dropped on a watched PC's card go to that PC's Downloads folder.</summary>
    private async void PcCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PcCard card } && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            && _remotes.FirstOrDefault(r => r.Name == card.Name) is { } target)
            await SendFiles(new() { target }, paths);
    }

    private void PcCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// One file at a time, to each PC at once, each followed in Transfers: read, packed and encrypted (whole, in
    /// memory — up to 512 MB), sent, then saved there.
    /// </summary>
    private async Task SendFiles(List<RemoteHistory> targets, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (Directory.Exists(path)) { Log($"{name} is a folder — send the files in it, or a .zip of it."); continue; }
            if (RemoteCommand.PlainFileName(name) == null) { Log($"{name}: Windows can't save it under that name (or it's over 128 characters) — rename it first."); continue; }
            var rows = targets.Select(t => (Target: t, Row: NewTransfer($"{name}  →  {t.Name}"))).ToList();
            rows.ForEach(r => r.Row.Phase("Reading the file…"));
            byte[] bytes;
            try
            {
                if (new FileInfo(path).Length > RemoteCommand.MaxFileBytes) { rows.ForEach(r => r.Row.Done("Not sent — it's over 512 MB.", false)); continue; }
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rows.ForEach(r => r.Row.Done($"Not sent — couldn't read it: {ex.Message}", false)); continue; }
            await Task.WhenAll(rows.Select(async r =>
            {
                r.Row.Phase("Packing and encrypting…");
                try
                {
                    string answer = await Task.Run(() => r.Target.Run("receive-file", name, "", bytes, (done, total) =>
                    {
                        r.Row.Progress(done, total, "Sending");
                        if (done == total) r.Row.Phase($"Saving on {r.Target.Name}…");
                    }));
                    int split = answer.IndexOf('\n');
                    string detail = LastLine(split < 0 ? "" : answer[(split + 1)..]);
                    bool ok = answer.StartsWith("ok");
                    r.Row.Done(ok ? $"Done — {detail} on {r.Target.Name}" : $"Failed — {detail}", ok);
                    Log($"Sending {name} to {r.Target.Name}: {(ok ? detail : "failed — " + detail)}");
                }
                catch (RemoteHistoryException ex) { r.Row.Done($"Failed — {ex.Message}", false); Log($"Sending {name} to {r.Target.Name}: {ex.Message}."); }
            }));
        }
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
        AllPcsButton.Visibility = _pcCards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        InsightsCard.Visibility = Visibility.Collapsed; // the other PC's week takes a moment to arrive
        RefreshInsights();
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
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); Raise(nameof(Summary)); } }
    /// <summary>The card's one line: CPU (and its temperature) and memory; the rest is in <see cref="Status"/>, on hover.</summary>
    public string Summary => string.Join(" · ", _status.Split('\n').Take(2));
    public bool Online { get => _online; set { _online = value; Raise(nameof(Online)); } }
    public bool Shown { get => _shown; set { _shown = value; Raise(nameof(Shown)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One file on its way to or from a watched PC, in the Transfers window. Updated from any thread.</summary>
internal sealed class TransferRow
{
    public readonly Border View;
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly Stopwatch _clock = new();
    private long _shownAt = -1000;

    public TransferRow(string title, System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 6, 0, 4), Maximum = 100, IsIndeterminate = true };
        _bar.SetResourceReference(Control.ForegroundProperty, "Accent");
        _bar.SetResourceReference(Control.BackgroundProperty, "PillBackground");
        _bar.BorderThickness = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(_bar, title);
        _detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var stack = new StackPanel();
        stack.Children.Add(name);
        stack.Children.Add(_bar);
        stack.Children.Add(_detail);
        View = new Border { Child = stack, Padding = new Thickness(0, 8, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
        View.SetResourceReference(Border.BorderBrushProperty, "Border");
    }

    /// <summary>A step with no measure of its own (reading, encrypting, saving): the bar just moves.</summary>
    public void Phase(string text) => _ui.InvokeAsync(() => { _bar.IsIndeterminate = true; _detail.Text = text; });

    /// <summary>Bytes on the wire: percent, amount, speed and time left, shown ten times a second at most.</summary>
    public void Progress(long done, long total, string verb)
    {
        if (!_clock.IsRunning) _clock.Start();
        long now = _clock.ElapsedMilliseconds;
        if (done < total && now - _shownAt < 100) return;
        _shownAt = now;
        double speed = done / Math.Max(0.05, _clock.Elapsed.TotalSeconds), percent = total > 0 ? 100.0 * done / total : 0;
        string left = done < total && speed > 0 ? $" · {Left((total - done) / speed)} left" : "";
        string text = $"{verb} — {percent:0}% · {CleanupService.FormatSize(done)} of {CleanupService.FormatSize(total)} · {CleanupService.FormatSize((long)speed)}/s{left}";
        _ui.InvokeAsync(() => { _bar.IsIndeterminate = false; _bar.Value = percent; _detail.Text = text; });
    }

    public void Done(string text, bool ok) => _ui.InvokeAsync(() =>
    {
        _bar.IsIndeterminate = false;
        _bar.Value = 100;
        _bar.SetResourceReference(Control.ForegroundProperty, ok ? "StatusGood" : "StatusBad");
        _detail.Text = text + (_clock.IsRunning ? $" ({Left(_clock.Elapsed.TotalSeconds)})" : "");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, ok ? "TextSecondary" : "StatusBad");
    });

    private static string Left(double seconds) => seconds < 60 ? $"{Math.Max(1, seconds):0} s" : $"{(int)(seconds / 60)} min {seconds % 60:0} s";
}

