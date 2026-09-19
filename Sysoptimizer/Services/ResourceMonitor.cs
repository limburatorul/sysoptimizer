using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    private readonly List<(string name, int active, int read, int write, int latency)> _diskCounters = new();
    private readonly List<int> _gpuUtilCounters = new();
    private readonly List<int> _gpuMemCounters = new();
    private readonly HashSet<string> _knownGpuUtilInstances = new();
    private readonly HashSet<string> _knownGpuMemInstances = new();

    private DateTime _lastSample = DateTime.UtcNow;
    private readonly Dictionary<string, (long sent, long recv)> _lastNetBytes = new();

    public ResourceMonitor()
    {
        _cpuCounter = _query.AddCounter(@"\Processor(_Total)\% Processor Time");

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
            if (counter >= 0) _gpuUtilCounters.Add(counter);
        }

        foreach (var instance in Pdh.EnumInstances("GPU Adapter Memory"))
        {
            if (!_knownGpuMemInstances.Add(instance)) continue;
            int counter = _query.AddCounter($@"\GPU Adapter Memory({instance})\Dedicated Usage");
            if (counter >= 0) _gpuMemCounters.Add(counter);
        }
    }

    public ResourceSnapshot Sample()
    {
        RefreshGpuCounters();
        _query.Collect();
        var snap = new ResourceSnapshot { CpuPercent = _query.Read(_cpuCounter) };

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
            snap.GpuPercent = Math.Min(100, _gpuUtilCounters.Sum(c => _query.Read(c)));
            snap.GpuMemUsedGB = _gpuMemCounters.Sum(c => _query.Read(c)) / 1073741824.0;
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

    public void Dispose() => _query.Dispose();
}
