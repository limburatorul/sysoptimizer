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

/// <summary>A laptop's battery; absent on a desktop.</summary>
public class BatteryReading
{
    public double Percent { get; set; }
    public bool PluggedIn { get; set; }
    public bool Charging { get; set; }
    public double Watts { get; set; } = double.NaN;       // charge or drain rate, when reported
    public double MinutesLeft { get; set; } = double.NaN; // on battery only
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
    public double MemUsedGB { get; set; }
    public double MemTotalGB { get; set; }
    public double MemPercent { get; set; }
    public bool GpuAvailable { get; set; }
    public double GpuPercent { get; set; }
    public double GpuMemUsedGB { get; set; }
    public List<DiskSnapshot> Disks { get; set; } = new();
    public List<NetSnapshot> Nets { get; set; } = new();
}
