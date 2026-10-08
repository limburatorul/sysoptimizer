using System.Diagnostics;
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
            _server = new HistoryServer(port, RemoteCrypto.ParseKey(RemoteKeyBox.Text)!, () => _liveJson);
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
        HealthCardBorder.Visibility = FreeMemoryButton.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        ResetLiveGraphs();
        if (_historyApp != null) HistoryAppClear_Click(this, new RoutedEventArgs());
        if (!IsLoaded) return;
        _animateChart = true;
        _ = LoadHistory();
        if (EventsList.IsVisible) _ = LoadEvents();
    }
}
