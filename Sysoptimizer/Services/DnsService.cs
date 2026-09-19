using System.Diagnostics;

namespace Sysoptimizer.Services;

public static class DnsService
{
    public static readonly (string Name, string? Primary, string? Secondary)[] Presets =
    {
        ("Automatic (DHCP)", null, null),
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112"),
    };

    public static bool SetDns(string adapterName, string? primary, string? secondary)
    {
        if (primary == null)
            return RunNetsh($"interface ip set dns name=\"{adapterName}\" dhcp");

        bool ok = RunNetsh($"interface ip set dns name=\"{adapterName}\" static {primary} primary");
        if (ok && secondary != null)
            ok = RunNetsh($"interface ip add dns name=\"{adapterName}\" {secondary} index=2");
        return ok;
    }

    private static bool RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
