using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sysoptimizer.Services;

public static class MemoryService
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static (int trimmed, long freed) TrimAll()
    {
        int trimmed = 0;
        long freedTotal = 0;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                long before1 = proc.WorkingSet64;
                if (EmptyWorkingSet(proc.Handle))
                {
                    proc.Refresh();
                    freedTotal += Math.Max(0, before1 - proc.WorkingSet64);
                    trimmed++;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return (trimmed, freedTotal);
    }
}
