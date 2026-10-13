using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sysoptimizer.Models;
using Sysoptimizer.Services;

namespace Sysoptimizer;

/// <summary>
/// Remote monitoring (see RemoteHistory.cs): this PC can share its history with a key, and the History and
/// Events tabs can show another PC's instead of this one's.
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
                if (key!.GetValue(name) is string data && data.Split('|') is [var address, var portText, var keyText]
                    && int.TryParse(portText, out int port) && RemoteCrypto.ParseKey(keyText) is { } secret)
                    _remotes.Add(new RemoteHistory(name, address, port, secret));
        // The PC picked last time stays picked: watching another PC is usually what this one is set up for.
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysoptimizer"))
            RefreshRemoteLists(key?.GetValue("Showing") as string);
        Closed += (_, _) => _server?.Dispose();
        var alertTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        alertTimer.Tick += (_, _) => PollRemoteAlerts();
        alertTimer.Start();
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
            _server = new HistoryServer(port, RemoteCrypto.ParseKey(RemoteKeyBox.Text)!, () => _liveJson, RunRemoteInstall);
        }
        catch (HttpListenerException ex)
        {
            RemoteServerCheck.IsChecked = false;
            RemoteServerStatus.Text = $"Couldn't listen on port {port} — {ex.Message}";
            return;
        }
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={port} profile=private");
        SaveServerSettings(on: true, port);

        var addresses = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString());
        RemoteServerStatus.Text = $"Sharing. On the other PC, add {Environment.MachineName} (or {string.Join(", ", addresses)}), port {port}, and this key. "
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

    private void RemoteNewKey_Click(object sender, RoutedEventArgs e)
    {
        RemoteKeyBox.Text = RemoteCrypto.NewKey();
        if (_server != null) StartServer(); // the old key stops working now
        else SaveServerSettings(on: false, int.TryParse(RemotePortBox.Text, out int port) ? port : DefaultPort);
        Log("New access key made — PCs using the old one can no longer read this PC's history.");
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

    // --- installs a watching PC sends here ---

    /// <summary>Runs on the server's thread: announced here first (tray, log, Events), then installed and reported back.</summary>
    private string RunRemoteInstall(RemoteInstall job)
    {
        string what = job.WingetId ?? job.FileName!;
        Dispatcher.Invoke(() => Alert($"remote-install:{Guid.NewGuid()}", $"A watching PC is installing {what} here"));
        string output;
        bool ok = job.WingetId != null ? WingetService.Install(job.WingetId, out output) : RunInstaller(job, out output);
        Dispatcher.Invoke(() => Log($"Remote install of {what}: {(ok ? "done." : "failed — " + LastLine(output))}"));
        return $"{(ok ? "ok" : "failed")}\n{output}";
    }

    private static bool RunInstaller(RemoteInstall job, out string output)
    {
        // The elevated user's own temp folder: other accounts on this PC can't swap the file before it runs.
        string folder = Path.Combine(Path.GetTempPath(), "Sysoptimizer-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, job.FileName!);
            File.WriteAllBytes(file, job.File!);
            // No hidden window: an installer without quiet options shows up for whoever sits at this PC.
            var psi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{file}\" /qn /norestart {job.Args}")
                : new ProcessStartInfo(file, job.Args);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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

    private static string LastLine(string text) =>
        text.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no details";

    // --- installing on the PC being shown ---

    private async Task InstallOn(RemoteHistory target, string what, Func<RemoteHistory, string> send)
    {
        Log($"{what}: installing on {target.Name}…");
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
        if (RemoteInstall.SafeFileName(name) == null) { Log($"{name}: only .exe and .msi files with names up to 128 characters can be sent."); return; }
        if (new FileInfo(dialog.FileName).Length > RemoteInstall.MaxFileBytes) { Log($"{name} is over 512 MB — too big to send; install it from a shared folder instead."); return; }
        string args = InstallArgsBox.Text.Trim();
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"{name}: couldn't read it — {ex.Message}"); return; }
        await InstallOn(target, name, t => t.InstallFile(name, bytes, args));
    }

    // --- watching another PC ---

    private async void RemoteAdd_Click(object sender, RoutedEventArgs e)
    {
        string address = RemoteAddressBox.Text.Trim();
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown) { RemoteAddStatus.Text = "Type the PC's name or IP address."; return; }
        if (!int.TryParse(RemoteAddPortBox.Text.Trim(), out int port) || port < 1 || port > 65535) { RemoteAddStatus.Text = "The port must be a number from 1 to 65535."; return; }
        if (RemoteCrypto.ParseKey(RemoteAddKeyBox.Text) is not { } secret) { RemoteAddStatus.Text = "That isn't an access key — copy it from the About tab on the other PC."; return; }

        RemoteAddButton.IsEnabled = false;
        RemoteAddStatus.Text = $"Connecting to {address}…";
        try
        {
            string name = await Task.Run(() => new RemoteHistory(address, address, port, secret).Hello());
            name = name.Replace('|', '_');
            _remotes.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            _remotes.Add(new RemoteHistory(name, address, port, secret));
            using (var key = Registry.CurrentUser.CreateSubKey(RemotePcsKey))
                key.SetValue(name, $"{address}|{port}|{RemoteAddKeyBox.Text.Trim()}");
            RemoteAddKeyBox.Clear();
            RemoteAddStatus.Text = $"Added {name}. Pick it under Showing, in the bar at the bottom.";
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
        string? selected = select ?? _source?.Name;
        HistorySourceCombo.Items.Clear();
        HistorySourceCombo.Items.Add(new ComboBoxItem { Content = "This PC" });
        foreach (var r in _remotes) HistorySourceCombo.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r });
        HistorySourceCombo.SelectedItem = HistorySourceCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as RemoteHistory)?.Name == selected)
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
        // What acts on this PC (health, cleanup, freeing memory) has no place while another one is shown.
        HealthCardBorder.Visibility = FreeMemoryButton.Visibility = UpgradeAllButton.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        // Installing follows the PC being shown.
        RemoteInstallPanel.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        InstallSelectedButton.Content = source == null ? "Install selected" : $"Install selected on {source.Name}";
        ResetLiveGraphs();
        if (_historyApp != null) HistoryAppClear_Click(this, new RoutedEventArgs());
        if (!IsLoaded) return;
        _animateChart = true;
        _ = LoadHistory();
        if (EventsList.IsVisible) _ = LoadEvents();
    }
}
