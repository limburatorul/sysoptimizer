using System.Diagnostics;

namespace Sysoptimizer.Services;

public static class BloatwareService
{
    public static readonly (string Name, string PackageName)[] KnownBloat =
    {
        ("3D Builder", "Microsoft.3DBuilder"),
        ("Weather", "Microsoft.BingWeather"),
        ("News", "Microsoft.BingNews"),
        ("Get Help", "Microsoft.GetHelp"),
        ("Tips", "Microsoft.Getstarted"),
        ("Office Hub", "Microsoft.MicrosoftOfficeHub"),
        ("Solitaire Collection", "Microsoft.MicrosoftSolitaireCollection"),
        ("Mixed Reality Portal", "Microsoft.MixedReality.Portal"),
        ("People", "Microsoft.People"),
        ("Skype", "Microsoft.SkypeApp"),
        ("Feedback Hub", "Microsoft.WindowsFeedbackHub"),
        ("Xbox App", "Microsoft.XboxApp"),
        ("Xbox Game Overlay", "Microsoft.XboxGameOverlay"),
        ("Xbox Gaming Overlay", "Microsoft.XboxGamingOverlay"),
        ("Xbox Identity Provider", "Microsoft.XboxIdentityProvider"),
        ("Xbox Speech To Text Overlay", "Microsoft.XboxSpeechToTextOverlay"),
        ("Phone Link", "Microsoft.YourPhone"),
        ("Groove Music", "Microsoft.ZuneMusic"),
        ("Movies & TV", "Microsoft.ZuneVideo"),
        ("To Do", "Microsoft.Todos"),
        ("Teams", "MicrosoftTeams"),
        ("Clipchamp", "Clipchamp.Clipchamp"),
        ("Cortana", "Microsoft.549981C3F5F10"),
    };

    public static bool IsInstalled(string packageName)
    {
        var (ok, output) = RunPowerShell($"Get-AppxPackage -Name {packageName} | Select-Object -First 1 Name");
        return ok && output.Contains(packageName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Remove(string packageName, out string message)
    {
        var (ok, output) = RunPowerShell(
            $"Get-AppxPackage -Name {packageName} -AllUsers | Remove-AppxPackage -AllUsers; " +
            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {packageName} | Remove-AppxProvisionedPackage -Online");
        message = ok ? "removed" : FirstLine(output);
        return ok;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "failed";

    private static (bool ok, string output) RunPowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
