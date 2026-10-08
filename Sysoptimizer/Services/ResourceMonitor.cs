using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Sysoptimizer.Models;

namespace Sysoptimizer.Services;

/// <summary>Polls the same PDH counters as Task Manager's Performance tab.</summary>
public class ResourceMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private readonly Pdh.Query _query = new();
    private readonly int _cpuCounter;
    private readonly int _cpuFreqCounter;
    private readonly double _cpuBaseMHz;
    private readonly List<int> _cpuThreadCounters = new();
    private readonly List<(string name, int active, int read, int write, int latency)> _diskCounters = new();
    private readonly List<(int Counter, int Pid)> _gpuUtilCounters = new();
    private readonly List<int> _gpuMemCounters = new();
    // Opening LibreHardwareMonitor takes a second or two (it probes every chip); never let it delay the first frame.
    private readonly Task<SensorService?> _sensors;
    public string? SensorError { get; private set; }
    private readonly HashSet<string> _knownGpuUtilInstances = new();
    private readonly HashSet<string> _knownGpuMemInstances = new();

    private DateTime _lastSample = DateTime.UtcNow;
    private readonly Dictionary<string, (long sent, long recv)> _lastNetBytes = new();

    public ResourceMonitor()
    {
        _sensors = Task.Run(() =>
        {
            try { return new SensorService(); }
            catch (Exception ex) { SensorError = ex.Message; return null; } // temperatures stay blank; the UI says why
        });
        _cpuCounter = _query.AddCounter(@"\Processor(_Total)\% Processor Time");
        _cpuFreqCounter = _query.AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        _cpuBaseMHz = ReadBaseClockMHz();
        // ponytail: Processor(N) numbers only the first 64 logical processors; switch to Processor Information(group,N) if a >64-thread machine ever matters.
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            int counter = _query.AddCounter($@"\Processor({i})\% Processor Time");
            if (counter >= 0) _cpuThreadCounters.Add(counter);
        }

        foreach (var instance in Pdh.EnumInstances("PhysicalDisk"))
        {
            if (instance == "_Total") continue;
            int active = _query.AddCounter($@"\PhysicalDisk({instance})\% Disk Time");
            int read = _query.AddCounter($@"\PhysicalDisk({instance})\Disk Read Bytes/sec");
            int write = _query.AddCounter($@"\PhysicalDisk({instance})\Disk Write Bytes/sec");
            int latency = _query.AddCounter($@"\PhysicalDisk({instance})\Avg. Disk sec/Transfer");
            _diskCounters.Add((instance, active, read, write, latency));
        }

        RefreshGpuCounters();

        _query.Collect();
        Thread.Sleep(150); // first sample after Collect() is always 0 — PDH needs two ticks to compute a rate
        _query.Collect();
    }

    /// <summary>
    /// "GPU Engine" instances exist only while a process holds a 3D context open, so the
    /// instance list changes constantly — re-scan every sample and adopt newly appeared ones.
    /// ponytail: counters for instances that later disappear are never removed, they just read 0; a
    /// long-running session accumulates stale handles — recreate the query if that ever matters.
    /// </summary>
    private void RefreshGpuCounters()
    {
        foreach (var instance in Pdh.EnumInstances("GPU Engine"))
        {
            if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_knownGpuUtilInstances.Add(instance)) continue;
            int counter = _query.AddCounter($@"\GPU Engine({instance})\Utilization Percentage");
            var pid = Regex.Match(instance, @"pid_(\d+)_");
            if (counter >= 0) _gpuUtilCounters.Add((counter, pid.Success ? int.Parse(pid.Groups[1].Value) : 0));
        }

        foreach (var instance in Pdh.EnumInstances("GPU Adapter Memory"))
        {
            if (!_knownGpuMemInstances.Add(instance)) continue;
            int counter = _query.AddCounter($@"\GPU Adapter Memory({instance})\Dedicated Usage");
            if (counter >= 0) _gpuMemCounters.Add(counter);
        }
    }

    /// <summary>Base clock speed in MHz, from the same registry value Task Manager reads (~MHz under the first logical processor's key).</summary>
    private static double ReadBaseClockMHz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    public ResourceSnapshot Sample()
    {
        RefreshGpuCounters();
        _query.Collect();
        double perfPercent = _cpuFreqCounter >= 0 ? _query.Read(_cpuFreqCounter) : 100;
        var snap = new ResourceSnapshot
        {
            CpuPercent = _query.Read(_cpuCounter),
            CpuThreads = _cpuThreadCounters.Select(c => Math.Min(100, _query.Read(c))).ToList(),
            CpuFrequencyGHz = _cpuBaseMHz * perfPercent / 100.0 / 1000.0,
            UptimeSeconds = Environment.TickCount64 / 1000.0,
        };

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            snap.MemTotalGB = mem.ullTotalPhys / 1073741824.0;
            snap.MemUsedGB = snap.MemTotalGB - mem.ullAvailPhys / 1073741824.0;
            snap.MemPercent = mem.dwMemoryLoad;
        }

        foreach (var (name, active, read, write, latency) in _diskCounters)
        {
            snap.Disks.Add(new DiskSnapshot
            {
                Name = Regex.Replace(name, @"^\d+\s*", "Disk "),
                ActivePercent = Math.Min(100, _query.Read(active)),
                ReadMBs = _query.Read(read) / 1048576.0,
                WriteMBs = _query.Read(write) / 1048576.0,
                LatencyMs = _query.Read(latency) * 1000.0,
            });
        }

        snap.GpuAvailable = _gpuUtilCounters.Count > 0;
        if (snap.GpuAvailable)
        {
            foreach (var (counter, pid) in _gpuUtilCounters)
            {
                double value = _query.Read(counter);
                if (value > 0) snap.GpuByPid[pid] = snap.GpuByPid.GetValueOrDefault(pid) + value;
            }
            snap.GpuPercent = Math.Min(100, snap.GpuByPid.Values.Sum());
            snap.GpuMemUsedGB = _gpuMemCounters.Sum(c => _query.Read(c)) / 1073741824.0;
        }

        // Windows' own battery report: always there on a laptop, no driver needed (the sensor library adds the rate).
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (!power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) && !power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Unknown))
            snap.Battery = new BatteryReading
            {
                Percent = power.BatteryLifePercent * 100,
                PluggedIn = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                Charging = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging),
                MinutesLeft = power.BatteryLifeRemaining > 0 ? power.BatteryLifeRemaining / 60.0 : double.NaN,
            };

        if (_sensors is { IsCompletedSuccessfully: true, Result: { } sensors })
        {
            try
            {
                var r = sensors.Read();
                (snap.CpuTempC, snap.GpuTempC, snap.CpuWatts, snap.GpuWatts) = (r.CpuC, r.GpuC, r.CpuWatts, r.GpuWatts);
                snap.Fans = r.Fans.Select(f => new FanReading { Name = f.Name, Rpm = f.Rpm }).ToList();
                if (snap.Battery != null) snap.Battery.Watts = r.BatteryWatts;
            }
            catch (Exception ex) { SensorError = ex.Message; } // blanks this sample's sensor readings only
        }

        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.001, (now - _lastSample).TotalSeconds);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var stats = nic.GetIPv4Statistics();
            var (prevSent, prevRecv) = _lastNetBytes.TryGetValue(nic.Id, out var v) ? v : (stats.BytesSent, stats.BytesReceived);
            _lastNetBytes[nic.Id] = (stats.BytesSent, stats.BytesReceived);

            double upMbps = Math.Max(0, (stats.BytesSent - prevSent) * 8 / seconds / 1_000_000);
            double downMbps = Math.Max(0, (stats.BytesReceived - prevRecv) * 8 / seconds / 1_000_000);
            if (upMbps < 0.01 && downMbps < 0.01 && nic.Speed <= 0) continue;

            var (latencyMs, latencyOk) = PingGateway(nic);
            snap.Nets.Add(new NetSnapshot
            {
                Name = nic.Name,
                UpMbps = upMbps,
                DownMbps = downMbps,
                LinkSpeedMbps = nic.Speed / 1_000_000.0,
                LatencyMs = latencyMs,
                LatencyAvailable = latencyOk,
            });
        }
        _lastSample = now;

        return snap;
    }

    /// <summary>Round-trip time to the adapter's default gateway — the closest thing to a per-adapter "latency" a NIC exposes.</summary>
    private static (double ms, bool ok) PingGateway(NetworkInterface nic)
    {
        var gateway = nic.GetIPProperties().GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
        if (gateway == null) return (0, false);

        try
        {
            using var ping = new Ping();
            var reply = ping.Send(gateway, 300);
            return reply?.Status == IPStatus.Success ? (reply.RoundtripTime, true) : (0, false);
        }
        catch
        {
            return (0, false);
        }
    }

    public void Dispose()
    {
        _query.Dispose();
        if (_sensors.IsCompletedSuccessfully) _sensors.Result?.Dispose();
    }
}
