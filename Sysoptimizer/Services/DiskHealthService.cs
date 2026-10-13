using System.Management;
using LibreHardwareMonitor.Hardware;
using Sysoptimizer.Models;

namespace Sysoptimizer.Services;

/// <summary>
/// Each drive's health, once a minute: what the drive reports about itself (SMART / NVMe — temperature and
/// its warning limit, life left, spare blocks, power-on hours, data written, read by LibreHardwareMonitor)
/// and Windows' own verdict on it (Storage Spaces' HealthStatus, which covers hard drives too).
/// </summary>
public sealed class DiskHealthService : IDisposable
{
    private readonly Computer _computer = new() { IsStorageEnabled = true };
    private List<DriveHealth> _latest = new();
    private DateTime _readAt = DateTime.MinValue;
    private bool _reading;

    public DiskHealthService() => _computer.Open();

    /// <summary>The last reading; asks for a fresh one in the background when it's a minute old.</summary>
    public List<DriveHealth> Latest()
    {
        if (!_reading && DateTime.UtcNow - _readAt > TimeSpan.FromMinutes(1))
        {
            _reading = true;
            Task.Run(() =>
            {
                try { _latest = Read(); }
                finally { (_readAt, _reading) = (DateTime.UtcNow, false); }
            });
        }
        return _latest;
    }

    private List<DriveHealth> Read()
    {
        var verdicts = WindowsVerdicts();
        var drives = new List<DriveHealth>();
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            hw.Update();
            float? Value(SensorType type, string name) => hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value;
            var temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name is "Temperature" or "Composite Temperature")?.Value;
            string model = hw.Name.Trim();
            // Two drives of the same model take Windows' verdicts in order.
            int same = drives.Count(d => d.Name == model);
            var verdict = verdicts.Where(v => v.Model == model).Select(v => v.Health).ElementAtOrDefault(same);
            drives.Add(new DriveHealth
            {
                Name = model,
                TempC = temp ?? double.NaN,
                WarnTempC = Value(SensorType.Temperature, "Warning Temperature") ?? double.NaN,
                LifePercent = Value(SensorType.Level, "Life") ?? double.NaN,
                SpareBelowThreshold = Value(SensorType.Level, "Available Spare") is { } spare && Value(SensorType.Level, "Available Spare Threshold") is { } floor && spare < floor,
                PowerOnHours = Value(SensorType.Factor, "Power On Hours") ?? double.NaN,
                WrittenTB = (Value(SensorType.Data, "Data Written") ?? float.NaN) / 1000,
                WindowsHealth = verdict,
            });
        }
        return drives;
    }

    /// <summary>Model → "Healthy", "Warning" or "Unhealthy", from MSFT_PhysicalDisk; empty if Windows won't say.</summary>
    private static List<(string Model, string Health)> WindowsVerdicts()
    {
        var list = new List<(string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, HealthStatus FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in searcher.Get())
                using (disk)
                    list.Add(((disk["FriendlyName"] as string ?? "").Trim(), Convert.ToUInt16(disk["HealthStatus"]) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" }));
        }
        catch (ManagementException) { } // no Storage WMI provider (older or trimmed Windows): the drive's own figures still show
        return list;
    }

    public void Dispose() => _computer.Close();
}
