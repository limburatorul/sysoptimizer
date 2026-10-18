using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using HidSharp;
using Sysoptimizer.Models;
using static Sysoptimizer.Services.PeripheralReports;

namespace Sysoptimizer.Services;

/// <summary>
/// Wireless mice, keyboards, headsets and controllers that report their battery, read every 5 minutes:
/// 1. what Windows itself shows in Settings → Bluetooth (the devices' battery property);
/// 2. Xbox controllers through XInput;
/// 3. Logitech receivers (Lightspeed, Unifying, Bolt) and wired Logitech devices through HID++ 2.0 — read requests only;
/// 4. DualShock 4 / DualSense through their input reports — nothing is sent to them at all.
/// Every HID handle is opened shared, so G HUB, Steam and games keep using the devices as before.
/// </summary>
public sealed class PeripheralBatteryService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private List<PeripheralBattery> _latest = new();
    private DateTime _readAt = DateTime.MinValue;
    private bool _reading;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private static bool UserJustActive()
    {
        var info = new LASTINPUTINFO { Size = 8 };
        return GetLastInputInfo(ref info) && (uint)Environment.TickCount - info.Time < 1000;
    }

    /// <summary>
    /// The last reading; asks for a fresh one in the background when it's 5 minutes old. A Lightspeed mouse sleeps
    /// seconds after it stops moving and then doesn't answer its receiver, so the read waits for the user's next input.
    /// ponytail: any input counts, keyboard included; a mouse still asleep is retried 30 s later, its last reading kept meanwhile.
    /// </summary>
    public List<PeripheralBattery> Latest()
    {
        if (!_reading && DateTime.UtcNow - _readAt > Every && UserJustActive())
        {
            _reading = true;
            Task.Run(() =>
            {
                try
                {
                    // A Logitech mouse asleep doesn't answer its receiver: keep its last reading rather than drop it.
                    // ponytail: kept until Sysoptimizer restarts, so an unpaired mouse lingers until then.
                    var fresh = Read();
                    foreach (var old in _latest.Where(o => o.Via.StartsWith("Logitech") && !fresh.Any(f => f.Name == o.Name)))
                    {
                        old.Asleep = true;
                        fresh.Add(old);
                    }
                    _latest = fresh;
                }
                finally
                {
                    bool asleep = _latest.Any(p => p.Asleep);
                    (_readAt, _reading) = (asleep ? DateTime.UtcNow - Every + TimeSpan.FromSeconds(30) : DateTime.UtcNow, false);
                }
            });
        }
        return _latest;
    }

    public static List<PeripheralBattery> Read()
    {
        var found = new List<PeripheralBattery>();
        Layer(found, Bluetooth);
        Layer(found, XInput);
        Layer(found, Logitech);
        Layer(found, PlayStation);
        return found;
    }

    /// <summary>A layer that fails (a device unplugged mid-read, a missing DLL) leaves the others' readings standing.</summary>
    private static void Layer(List<PeripheralBattery> found, Action<List<PeripheralBattery>> read)
    {
        try { read(found); }
        catch (Exception ex) when (ex is IOException or TimeoutException or Win32Exception or DllNotFoundException
                                       or EntryPointNotFoundException or UnauthorizedAccessException or ObjectDisposedException)
        {
            AppLog.Write($"Wireless batteries, {read.Method.Name}: {ex.Message}");
        }
    }

    private static bool Listed(List<PeripheralBattery> found, string word) =>
        found.Any(f => f.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    // --- 1. The battery property Windows shows in Settings → Bluetooth ---

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid Fmtid; public uint Pid; }

    private static readonly DEVPROPKEY BluetoothBattery = new() { Fmtid = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };
    private static readonly DEVPROPKEY FriendlyName = new() { Fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };
    private static readonly DEVPROPKEY DeviceDesc = new() { Fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 2 };
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100, DEVPROP_TYPE_BYTE = 0x03, DEVPROP_TYPE_STRING = 0x12;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_PropertyW(uint devInst, in DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    private static void Bluetooth(List<PeripheralBattery> found)
    {
        if (CM_Get_Device_ID_List_SizeW(out uint length, null, CM_GETIDLIST_FILTER_PRESENT) != 0) return;
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(null, buffer, length, CM_GETIDLIST_FILTER_PRESENT) != 0) return;
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) continue;
            var percent = new byte[1];
            uint size = 1;
            if (CM_Get_DevNode_PropertyW(node, BluetoothBattery, out uint type, percent, ref size, 0) != 0 || type != DEVPROP_TYPE_BYTE) continue;
            found.Add(new PeripheralBattery { Name = Text(node, FriendlyName) ?? Text(node, DeviceDesc) ?? "Bluetooth device", Via = "Bluetooth", Percent = Math.Min((int)percent[0], 100) });
        }
    }

    private static string? Text(uint node, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(node, key, out _, null, ref size, 0); // asks the length
        if (size == 0) return null;
        var bytes = new byte[size];
        if (CM_Get_DevNode_PropertyW(node, key, out uint type, bytes, ref size, 0) != 0 || type != DEVPROP_TYPE_STRING) return null;
        return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    // --- 2. XInput ---

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte Type, Level; }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetBatteryInformation(int userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

    private static void XInput(List<PeripheralBattery> found)
    {
        // ponytail: an Xbox controller on Bluetooth is already listed by layer 1, so XInput steps aside when one is;
        // a second Xbox pad on the wireless adapter at the same time would then be missed.
        if (Listed(found, "Xbox")) return;
        for (int i = 0; i < 4; i++)
        {
            if (XInputGetBatteryInformation(i, 0 /* BATTERY_DEVTYPE_GAMEPAD */, out var info) != 0) continue; // nothing in this slot
            if (XInputLevel(info.Type, info.Level) is not { } level) continue;
            found.Add(new PeripheralBattery { Name = i == 0 ? "Xbox controller" : $"Xbox controller {i + 1}", Via = "XInput", Level = level });
        }
    }

    // --- 3. Logitech HID++ 2.0 ---

    private const byte SoftwareId = 0x0B; // ours, so answers meant for G HUB are told apart
    private static readonly byte[] DeviceSlots = { 1, 2, 3, 4, 5, 6, 0xFF }; // a receiver's paired devices, then a device on its own cable

    private static void Logitech(List<PeripheralBattery> found)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(0x046D))
        {
            // The HID++ long-report collection: 20-byte reports on a vendor usage page. Never the mouse or keyboard collections.
            if (device.GetMaxOutputReportLength() != 20 || device.GetMaxInputReportLength() != 20 || !VendorPage(device)) continue;
            var shared = new OpenConfiguration();
            shared.SetOption(OpenOption.Exclusive, false);
            if (!device.TryOpen(shared, out HidStream stream)) continue;
            using (stream)
            {
                stream.ReadTimeout = 300;
                foreach (byte slot in DeviceSlots)
                    if (LogitechBattery(stream, slot) is { } battery) found.Add(battery);
            }
        }
    }

    private static bool VendorPage(HidDevice device)
    {
        try { return device.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u >> 16 >= 0xFF00)); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }

    private static PeripheralBattery? LogitechBattery(HidStream s, byte slot)
    {
        PeripheralBattery? battery = null;
        int unified = Index(s, slot, UnifiedBattery), status, voltage;
        if (unified < 0) return null; // nothing paired in this slot, or it's asleep: one timeout, not three
        if (unified > 0 && Ask(s, slot, (byte)unified, UnifiedBattery, 1) is { } u)
        {
            var (percent, level, charging) = Unified(u);
            battery = new PeripheralBattery { Percent = percent, Level = level, Charging = charging };
        }
        else if ((status = Index(s, slot, BatteryStatus)) > 0 && Ask(s, slot, (byte)status, BatteryStatus, 0) is { } st)
        {
            var (percent, charging) = Status(st);
            battery = new PeripheralBattery { Percent = percent, Charging = charging };
        }
        else if ((voltage = Index(s, slot, BatteryVoltage)) > 0 && Ask(s, slot, (byte)voltage, BatteryVoltage, 0) is { } v)
        {
            var (percent, mv, charging) = Voltage(v);
            battery = new PeripheralBattery { Percent = percent, Charging = charging, Via = $" · {mv / 1000.0:0.00} V, % estimated from it" };
        }
        if (battery == null) return null;
        battery.Name = Name(s, slot) ?? "Logitech device";
        battery.Via = (slot == 0xFF ? "Logitech HID++ (cable)" : "Logitech HID++ (receiver)") + battery.Via;
        return battery;
    }

    /// <summary>Root.getFeature: where this device keeps a feature; 0 when it hasn't got it, -1 when nothing answered.</summary>
    private static int Index(HidStream s, byte slot, ushort feature) =>
        Ask(s, slot, 0, Root, 0, (byte)(feature >> 8), (byte)feature) is { } r ? r[0] : -1;

    private static string? Name(HidStream s, byte slot)
    {
        if (Index(s, slot, DeviceName) is not (> 0 and var at) || Ask(s, slot, (byte)at, DeviceName, 0) is not { } count) return null;
        byte index = (byte)at;
        var name = new System.Text.StringBuilder();
        while (name.Length < count[0] && Ask(s, slot, index, DeviceName, 1, (byte)name.Length) is { } part)
        {
            int before = name.Length;
            foreach (byte b in part) { if (b == 0 || name.Length >= count[0]) break; name.Append((char)b); }
            if (name.Length == before) break; // the device sent nothing more
        }
        return name.Length > 0 ? name.ToString() : null;
    }

    /// <summary>
    /// One HID++ request and its answer (16 parameter bytes), or null on an error or no answer in time.
    /// Refuses anything outside the read-only list in <see cref="PeripheralReports.HidppAllowed"/>.
    /// </summary>
    private static byte[]? Ask(HidStream s, byte slot, byte featureIndex, ushort feature, byte function, params byte[] args)
    {
        if (!HidppAllowed(feature, function)) throw new InvalidOperationException($"HID++ 0x{feature:X4} function {function} is not a read Sysoptimizer allows");
        var request = new byte[20];
        (request[0], request[1], request[2], request[3]) = (0x11, slot, featureIndex, (byte)(function << 4 | SoftwareId));
        args.CopyTo(request, 4);
        s.Write(request);

        var answer = new byte[20];
        var until = DateTime.UtcNow.AddMilliseconds(s.ReadTimeout);
        while (DateTime.UtcNow < until)
        {
            try { if (s.Read(answer) < 7) continue; }
            catch (TimeoutException) { return null; }
            if (answer[0] != 0x11 || answer[1] != slot) continue; // another device's, or a notification
            if (answer[2] == featureIndex && answer[3] == request[3]) return answer[4..];
            if (answer[2] == 0xFF && answer[3] == featureIndex && answer[4] == request[3]) return null; // the device's error
        }
        return null;
    }

    // --- 4. DualShock 4 / DualSense: listen to the reports they send anyway ---

    private static void PlayStation(List<PeripheralBattery> found)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(0x054C))
        {
            var model = SonyModel(device.ProductID);
            string name = model switch { Sony.DualShock4 => "DualShock 4", Sony.DualSense => "DualSense", Sony.DualSenseEdge => "DualSense Edge", _ => "" };
            if (model == Sony.None || Listed(found, name)) continue;
            var shared = new OpenConfiguration();
            shared.SetOption(OpenOption.Exclusive, false);
            if (!device.TryOpen(shared, out HidStream stream)) continue;
            using (stream)
            {
                stream.ReadTimeout = 500;
                var report = new byte[Math.Max(device.GetMaxInputReportLength(), 1)];
                var reading = new PeripheralBattery { Name = name, Via = "PlayStation, USB" };
                for (int i = 0; i < 4; i++) // the first report can be a short one
                {
                    int n;
                    try { n = stream.Read(report); }
                    catch (TimeoutException) { break; } // a pad that sends nothing (asleep) is listed as unknown
                    var (known, percent, charging, bluetooth) = SonyBattery(model, report.AsSpan(0, n));
                    reading.Via = bluetooth ? "PlayStation, Bluetooth" : "PlayStation, USB";
                    if (!known) continue;
                    (reading.Percent, reading.Charging) = (percent, charging);
                    break;
                }
                found.Add(reading);
            }
        }
    }
}
