using System.Runtime.InteropServices;
using System.Text;

namespace Sysoptimizer.Services;

/// <summary>Thin wrapper over pdh.dll — the same counters Task Manager's Performance tab reads.</summary>
internal static class Pdh
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhEnumObjectItemsW(
        string? szDataSource, string? szMachineName, string szObjectName,
        IntPtr mszCounterList, ref uint pcchCounterListLength,
        char[]? mszInstanceList, ref uint pcchInstanceListLength,
        uint dwDetailLevel, uint dwFlags);

    public sealed class Query : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly List<IntPtr> _counters = new();

        public Query() => PdhOpenQueryW(null, IntPtr.Zero, out _handle);

        /// <returns>A counter index to pass to Read(), or -1 if the path doesn't exist on this machine.</returns>
        public int AddCounter(string path)
        {
            if (PdhAddEnglishCounterW(_handle, path, IntPtr.Zero, out var counter) != 0) return -1;
            _counters.Add(counter);
            return _counters.Count - 1;
        }

        public void Collect() => PdhCollectQueryData(_handle);

        public double Read(int index)
        {
            if (index < 0 || index >= _counters.Count) return 0;
            if (PdhGetFormattedCounterValue(_counters[index], PDH_FMT_DOUBLE, IntPtr.Zero, out var value) != 0) return 0;
            return value.doubleValue;
        }

        public void Dispose() => PdhCloseQuery(_handle);
    }

    /// <summary>Instance names for a counter object, e.g. "PhysicalDisk" -> ["0 C:", "1 D:", "_Total"].</summary>
    public static string[] EnumInstances(string objectName)
    {
        uint counterLen = 0, instanceLen = 0;
        PdhEnumObjectItemsW(null, null, objectName, IntPtr.Zero, ref counterLen, null, ref instanceLen, 100, 0);
        if (instanceLen == 0) return Array.Empty<string>();

        var buffer = new char[instanceLen];
        // Passing a null counter-list buffer requires its length back at 0 too, or PDH treats the
        // mismatched (null pointer, nonzero length) pair as an error and returns nothing.
        counterLen = 0;
        PdhEnumObjectItemsW(null, null, objectName, IntPtr.Zero, ref counterLen, buffer, ref instanceLen, 100, 0);

        var text = new string(buffer);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
