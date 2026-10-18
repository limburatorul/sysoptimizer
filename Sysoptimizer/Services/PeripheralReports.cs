namespace Sysoptimizer.Services;

/// <summary>
/// The byte layouts behind wireless-device batteries, kept apart from the I/O so they can be checked without the
/// hardware. Written from the public layouts (Logitech's HID++ 2.0 feature documents, the community-documented
/// DualShock 4 / DualSense input reports, the XInput headers) — no code taken from any other project.
/// </summary>
public static class PeripheralReports
{
    // --- Logitech HID++ 2.0 ---

    public const ushort Root = 0x0000, DeviceName = 0x0005, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004;

    /// <summary>
    /// The only HID++ requests Sysoptimizer ever sends: each one reads. Root.getFeature (asks a feature's index),
    /// DeviceName.getCount / getName, BatteryStatus.getLevel, BatteryVoltage.getVoltage, UnifiedBattery.getStatus.
    /// Nothing that sets, nothing for lighting, DPI or firmware.
    /// </summary>
    private static readonly HashSet<(ushort Feature, byte Function)> HidppReads = new()
    {
        (Root, 0), (DeviceName, 0), (DeviceName, 1), (BatteryStatus, 0), (BatteryVoltage, 0), (UnifiedBattery, 1),
    };

    public static bool HidppAllowed(ushort feature, byte function) => HidppReads.Contains((feature, function));

    /// <summary>0x1004 getStatus: [0] charge %, [1] level flags (1 critical, 2 low, 4 good, 8 full), [2] 1–3 = on the charger.</summary>
    public static (double Percent, string? Level, bool Charging) Unified(ReadOnlySpan<byte> p)
    {
        string? level = (p[1] & 8) != 0 ? "Full" : (p[1] & 4) != 0 ? "Good" : (p[1] & 2) != 0 ? "Low" : (p[1] & 1) != 0 ? "Empty" : null;
        // A device that only knows levels sends 0 as its percentage.
        return (p[0] is > 0 and <= 100 ? p[0] : double.NaN, level, p[2] is 1 or 2 or 3);
    }

    /// <summary>0x1000 getLevel: [0] charge %, [2] status (1 recharging, 2 almost full, 3 full, 4 slow recharge).</summary>
    public static (double Percent, bool Charging) Status(ReadOnlySpan<byte> p) =>
        (p[0] is > 0 and <= 100 ? p[0] : double.NaN, p[2] is 1 or 2 or 3 or 4);

    /// <summary>0x1001 getVoltage: [0..1] millivolts (big-endian), [2] flags, bit 7 = on external power.</summary>
    public static (double Percent, int Millivolts, bool Charging) Voltage(ReadOnlySpan<byte> p)
    {
        int mv = p[0] << 8 | p[1];
        return (VoltagePercent(mv), mv, (p[2] & 0x80) != 0);
    }

    // ponytail: one generic single-cell Li-ion discharge curve for every device; the mouse's own maker table would
    // read a few percent differently. Replace with per-model tables only if a tester shows a real gap.
    private static readonly (int Mv, double Percent)[] LiIon =
    {
        (3500, 0), (3670, 10), (3710, 20), (3750, 30), (3790, 40), (3830, 50), (3870, 60), (3920, 70), (3970, 80), (4050, 90), (4150, 100),
    };

    public static double VoltagePercent(int mv)
    {
        if (mv <= 0) return double.NaN;
        if (mv <= LiIon[0].Mv) return 0;
        for (int i = 1; i < LiIon.Length; i++)
            if (mv <= LiIon[i].Mv)
            {
                var (a, b) = (LiIon[i - 1], LiIon[i]);
                return Math.Round(a.Percent + (b.Percent - a.Percent) * (mv - a.Mv) / (b.Mv - a.Mv));
            }
        return 100;
    }

    // --- Sony DualShock 4 / DualSense (input reports only; nothing is ever sent to the controller) ---

    public enum Sony { None, DualShock4, DualSense, DualSenseEdge }

    public static Sony SonyModel(int productId) => productId switch
    {
        0x05C4 or 0x09CC or 0x0BA0 => Sony.DualShock4, // 0x0BA0 = Sony's USB wireless adapter
        0x0CE6 => Sony.DualSense,
        0x0DF2 => Sony.DualSenseEdge,
        _ => Sony.None,
    };

    /// <summary>
    /// The battery from one input report. Known = false when this report has no battery in it: over Bluetooth the
    /// controllers send a short report until a game asks for the full one, and Sysoptimizer never asks (asking hides
    /// the controller from older games until it's switched off).
    /// DS4: USB report 0x01 byte 30, Bluetooth 0x11 byte 32 — low nibble level, bit 4 cable. Wired 0–10 (11 = full), wireless 0–9.
    /// DualSense: USB 0x01 byte 53, Bluetooth 0x31 byte 54 — low nibble level 0–10, high nibble 1 = charging, 2 = full.
    /// </summary>
    public static (bool Known, double Percent, bool Charging, bool Bluetooth) SonyBattery(Sony model, ReadOnlySpan<byte> r)
    {
        if (r.Length == 0) return default;
        if (model == Sony.DualShock4)
        {
            int at = r[0] == 0x01 && r.Length >= 64 ? 30 : r[0] == 0x11 && r.Length >= 33 ? 32 : -1;
            if (at < 0) return (false, double.NaN, false, r[0] != 0x01 || r.Length < 64);
            int level = r[at] & 0x0F;
            bool cable = (r[at] & 0x10) != 0;
            // ponytail: clones often leave the byte at 0; 0% with no cable is read as "doesn't say", not as a dead pad.
            if (level == 0 && !cable) return (false, double.NaN, false, at == 32);
            return (true, Math.Min(level * 10, 100), cable && level <= 10, at == 32);
        }
        if (model is Sony.DualSense or Sony.DualSenseEdge)
        {
            int at = r[0] == 0x01 && r.Length >= 64 ? 53 : r[0] == 0x31 && r.Length >= 55 ? 54 : -1;
            if (at < 0) return (false, double.NaN, false, r[0] != 0x01 || r.Length < 64);
            int level = r[at] & 0x0F, state = r[at] >> 4;
            return (true, Math.Min(level * 10, 100), state is 1 or 2, at == 54);
        }
        return default;
    }

    // --- XInput (Xbox controllers and the ones that pretend to be) ---

    /// <summary>BATTERY_TYPE (0 disconnected, 1 wired, 2 alkaline, 3 NiMH, 0xFF unknown) and BATTERY_LEVEL 0–3: a level, never a percentage.</summary>
    public static string? XInputLevel(byte type, byte level) => type switch
    {
        0 => null,
        1 => "Wired",
        _ => level switch { 0 => "Empty", 1 => "Low", 2 => "Medium", _ => "Full" },
    };
}
