using System.IO;

namespace Sysoptimizer.Services;

/// <summary>
/// What the app did and what went wrong, kept on disk — the Log panel only lasts until the app closes, and a
/// crash on a watched PC is read from here by the watching one. ProgramData\Sysoptimizer\sysoptimizer.log,
/// 2 MB, then it moves to .old and starts again.
/// </summary>
public static class AppLog
{
    public static string File { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sysoptimizer", "sysoptimizer.log");
    private const long MaxBytes = 2 << 20;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                if (new FileInfo(File) is { Exists: true, Length: > MaxBytes }) System.IO.File.Move(File, File + ".old", overwrite: true);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message.Replace("\r", "").Replace("\n", "\n    ")}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a log that can't be written mustn't take the app down with it
        }
    }

    /// <summary>The end of the log, whole lines, about <paramref name="maxChars"/> long.</summary>
    public static string Tail(int maxChars = 200_000)
    {
        lock (Gate)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                string text = reader.ReadToEnd();
                return text.Length <= maxChars ? text : text[(text.IndexOf('\n', text.Length - maxChars) + 1)..];
            }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
        }
    }
}
