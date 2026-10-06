using LibreHardwareMonitor.Hardware;

namespace Sysoptimizer.Services;

/// <summary>
/// CPU/GPU temperatures via LibreHardwareMonitor. GPU temperature comes from the vendor library (no
/// driver); Ryzen/Intel CPU temperature needs the PawnIO kernel driver — without it the CPU reads NaN.
/// Not thread-safe: ResourceMonitor calls Read() from one sampling task at a time.
/// </summary>
public sealed class SensorService : IDisposable
{
    private static readonly string[] CpuPreference = { "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core Average" };
    private static readonly string[] GpuPreference = { "GPU Core", "GPU Hot Spot" };

    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };

    public SensorService() => _computer.Open();

    public (double CpuC, double GpuC) Read()
    {
        double cpu = double.NaN, gpu = double.NaN;
        foreach (var hardware in _computer.Hardware)
        {
            bool isCpu = hardware.HardwareType == HardwareType.Cpu;
            bool isGpu = hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
            if (!isCpu && !isGpu) continue;

            hardware.Update();
            double value = Pick(hardware, isCpu ? CpuPreference : GpuPreference);
            if (isCpu && double.IsNaN(cpu)) cpu = value;
            if (isGpu && double.IsNaN(gpu)) gpu = value; // first GPU wins — the discrete card is listed before an iGPU
        }
        return (cpu, gpu);
    }

    private static double Pick(IHardware hardware, string[] preference)
    {
        var temps = hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0).ToList();
        foreach (var name in preference)
        {
            var match = temps.FirstOrDefault(s => s.Name == name);
            if (match != null) return match.Value!.Value;
        }
        return temps.Count > 0 ? temps[0].Value!.Value : double.NaN;
    }

    public void Dispose() => _computer.Close();
}
