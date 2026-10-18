namespace Sysoptimizer.Models;

public class DiskSnapshot
{
    public string Name { get; set; } = "";
    public double ActivePercent { get; set; }
    public double ReadMBs { get; set; }
    public double WriteMBs { get; set; }
    public double LatencyMs { get; set; }
}

public class NetSnapshot
{
    public string Name { get; set; } = "";
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
    public double LinkSpeedMbps { get; set; }
    public double LatencyMs { get; set; }
    public bool LatencyAvailable { get; set; }
}

public class FanReading
{
    public string Name { get; set; } = "";
    public double Rpm { get; set; }
}

public class DriveSpace
{
    public string Name { get; set; } = "";
    public double FreeGB { get; set; }
    public double TotalGB { get; set; }
}

/// <summary>One drive's health: its own SMART / NVMe figures, and Windows' verdict on it.</summary>
public class DriveHealth
{
    public string Name { get; set; } = "";
    public double TempC { get; set; } = double.NaN;
    public double WarnTempC { get; set; } = double.NaN;       // NVMe drives say where their own warning starts
    public double LifePercent { get; set; } = double.NaN;     // SSDs: life left, 100 = new
    public bool SpareBelowThreshold { get; set; }             // NVMe: out of spare blocks — the drive's own alarm
    public double PowerOnHours { get; set; } = double.NaN;
    public double WrittenTB { get; set; } = double.NaN;
    public string? WindowsHealth { get; set; }                // Healthy, Warning, Unhealthy, Unknown; null if not known
}

/// <summary>A laptop's battery; absent on a desktop.</summary>
public class BatteryReading
{
    public double Percent { get; set; }
    public bool PluggedIn { get; set; }
    public bool Charging { get; set; }
    public double Watts { get; set; } = double.NaN;       // charge or drain rate, when reported
    public double MinutesLeft { get; set; } = double.NaN; // on battery only
}

/// <summary>A wireless mouse, keyboard, headset or controller that says how full its battery is.</summary>
public class PeripheralBattery
{
    public string Name { get; set; } = "";
    public string Via { get; set; } = "";                 // how it was read: Bluetooth, XInput, Logitech HID++, PlayStation
    public double Percent { get; set; } = double.NaN;     // NaN when the device gives only a level, or nothing
    public string? Level { get; set; }                    // Full / Good / Medium / Low / Empty / Wired, when that's all it says
    public bool Charging { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.Now;
    public bool Asleep { get; set; }                      // didn't answer this time: its last reading, kept

    public string Details => Asleep ? $"{Via} · asleep, read at {ReadAt:HH:mm}" : Via;

    public bool IsLow(int limit) => !Charging && (Percent <= limit || Level is "Low" or "Empty");

    public string Shown => Charging ? (double.IsNaN(Percent) ? "Charging" : $"Charging · {Percent:0}%")
        : !double.IsNaN(Percent) ? $"{Percent:0}%" : Level ?? "Unknown";
}

public class ResourceSnapshot
{
    public double CpuPercent { get; set; }
    public List<double> CpuThreads { get; set; } = new();
    public double CpuFrequencyGHz { get; set; }
    public double UptimeSeconds { get; set; }
    public double CpuTempC { get; set; } = double.NaN;
    public double GpuTempC { get; set; } = double.NaN;
    public double CpuWatts { get; set; } = double.NaN;
    public double GpuWatts { get; set; } = double.NaN;
    public List<FanReading> Fans { get; set; } = new();
    public BatteryReading? Battery { get; set; }
    public Dictionary<int, double> GpuByPid { get; set; } = new();
    public Dictionary<int, double> FpsByPid { get; set; } = new();
    public double Fps { get; set; } = double.NaN; // the app in front, when it draws frames (a game)
    public string? FpsApp { get; set; }
    public double MemUsedGB { get; set; }
    public double MemTotalGB { get; set; }
    public double MemPercent { get; set; }
    public bool GpuAvailable { get; set; }
    public double GpuPercent { get; set; }
    public double GpuMemUsedGB { get; set; }
    public List<DiskSnapshot> Disks { get; set; } = new();
    public List<NetSnapshot> Nets { get; set; } = new();
    public List<DriveSpace> Drives { get; set; } = new(); // fixed drives' free space, read every 30 s
    public List<DriveHealth> DriveHealth { get; set; } = new(); // read every minute
    public List<PeripheralBattery> Peripherals { get; set; } = new(); // read every 5 minutes
}
