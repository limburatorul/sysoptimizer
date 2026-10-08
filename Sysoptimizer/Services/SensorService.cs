using LibreHardwareMonitor.Hardware;

namespace Sysoptimizer.Services;

/// <param name="Fans">Each fan that is spinning, by name: the board's headers ("Fan 2") and the graphics card's ("GPU").</param>
/// <param name="BatteryWatts">How fast the battery charges or drains, when the laptop reports it.</param>
public sealed record SensorReading(double CpuC, double GpuC, double CpuWatts, double GpuWatts, List<(string Name, double Rpm)> Fans, double BatteryWatts);

/// <summary>
/// Temperatures, power draw and fan speeds via LibreHardwareMonitor. GPU readings come from the vendor
/// library (no driver); the CPU's and the motherboard's (fans) need the PawnIO kernel driver — without it
/// they read NaN / no fans.
/// Not thread-safe: ResourceMonitor calls Read() from one sampling task at a time.
/// </summary>
public sealed class SensorService : IDisposable
{
    private static readonly string[] CpuPreference = { "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core Average" };
    private static readonly string[] GpuPreference = { "GPU Core", "GPU Hot Spot" };
    private static readonly string[] CpuPowerPreference = { "Package", "CPU Package" };
    private static readonly string[] GpuPowerPreference = { "GPU Package", "GPU Power", "GPU Core" };

    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsBatteryEnabled = true };

    public SensorService() => _computer.Open();

    public SensorReading Read()
    {
        double cpu = double.NaN, gpu = double.NaN, cpuW = double.NaN, gpuW = double.NaN, batteryW = double.NaN;
        var fans = new List<(string, double)>();
        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu when double.IsNaN(cpu):
                    cpu = Pick(hardware, SensorType.Temperature, CpuPreference);
                    cpuW = Pick(hardware, SensorType.Power, CpuPowerPreference);
                    break;
                case HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel when double.IsNaN(gpu):
                    // first GPU wins — the discrete card is listed before an iGPU
                    gpu = Pick(hardware, SensorType.Temperature, GpuPreference);
                    gpuW = Pick(hardware, SensorType.Power, GpuPowerPreference);
                    fans.AddRange(Spinning(hardware).Select(f => (hardware.Sensors.Count(s => s.SensorType == SensorType.Fan) > 1 ? $"GPU {f.Name}" : "GPU", f.Rpm)));
                    break;
                case HardwareType.Motherboard:
                    // The fan headers live on the board's Super I/O chip, a sub-device.
                    foreach (var chip in hardware.SubHardware)
                    {
                        chip.Update();
                        fans.AddRange(Spinning(chip).Select(f => (f.Name.Replace("#", "").Replace("  ", " "), f.Rpm)));
                    }
                    break;
                case HardwareType.Battery:
                    batteryW = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value is > 0)?.Value ?? double.NaN;
                    break;
            }
        }
        return new SensorReading(cpu, gpu, cpuW, gpuW, fans, batteryW);
    }

    /// <summary>Fans turning now; a header with nothing plugged in reads 0 and isn't a fan worth listing.</summary>
    private static IEnumerable<(string Name, double Rpm)> Spinning(IHardware hardware) =>
        hardware.Sensors.Where(s => s.SensorType == SensorType.Fan && s.Value is > 0).Select(s => (s.Name, (double)s.Value!.Value));

    private static double Pick(IHardware hardware, SensorType type, string[] preference)
    {
        var sensors = hardware.Sensors.Where(s => s.SensorType == type && s.Value is > 0).ToList();
        foreach (var name in preference)
        {
            var match = sensors.FirstOrDefault(s => s.Name == name);
            if (match != null) return match.Value!.Value;
        }
        // Any temperature beats none; for power, only the named totals (a single core's figure isn't the CPU's).
        return type == SensorType.Temperature && sensors.Count > 0 ? sensors[0].Value!.Value : double.NaN;
    }

    public void Dispose() => _computer.Close();
}
