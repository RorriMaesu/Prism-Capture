using System;
using System.Diagnostics;
using System.IO;

namespace ScreenRecorder.App.Helpers;

internal static class Breadcrumbs
{
    private static readonly object Gate = new();

    private static string LogPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrismCapture");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "breadcrumbs.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            var pid = Environment.ProcessId;
            var tid = Environment.CurrentManagedThreadId;
            var line = $"{DateTime.UtcNow:O} | pid={pid} tid={tid} | {message}{Environment.NewLine}";

            try { Debug.WriteLine(line); } catch { }
            lock (Gate)
            {
                using var fs = new FileStream(
                    LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.WriteThrough);
                using var sw = new StreamWriter(fs);
                sw.Write(line);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Never throw from breadcrumbs.
        }
    }

    public static void Write(Exception ex)
    {
        Write(ex.ToString());
    }

    public static void Session(string label)
    {
        Write($"===== {label} =====");
    }
}
