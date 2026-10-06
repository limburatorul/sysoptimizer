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

public class ResourceSnapshot
{
    public double CpuPercent { get; set; }
    public List<double> CpuThreads { get; set; } = new();
    public double CpuFrequencyGHz { get; set; }
    public double UptimeSeconds { get; set; }
    public double CpuTempC { get; set; } = double.NaN;
    public double GpuTempC { get; set; } = double.NaN;
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
