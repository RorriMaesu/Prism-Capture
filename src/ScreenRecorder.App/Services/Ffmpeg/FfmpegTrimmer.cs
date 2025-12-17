using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRecorder.App.Services.Ffmpeg;

internal static class FfmpegTrimmer
{
    public static async Task TrimInPlaceAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required.", nameof(inputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found.", inputPath);
        }

        if (start < TimeSpan.Zero || duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Start must be >= 0 and duration must be > 0.");
        }

        if (!FfmpegAvMuxer.IsFfmpegAvailable(out var ffmpeg, out var msg))
        {
            throw new InvalidOperationException(msg ?? "ffmpeg not available.");
        }

        var dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var tempPath = Path.Combine(dir, $"{baseName}.trim.{Guid.NewGuid():N}{ext}");

        var inv = CultureInfo.InvariantCulture;
        var args = string.Join(" ", new[]
        {
            "-hide_banner",
            "-loglevel error",
            "-y",
            $"-i \"{inputPath}\"",
            $"-ss {start.TotalSeconds.ToString("0.###", inv)}",
            $"-t {duration.TotalSeconds.ToString("0.###", inv)}",
            "-map 0",
            "-c copy",
            "-avoid_negative_ts make_zero",
            $"\"{tempPath}\""
        });

        try
        {
            await RunProcessAsync(ffmpeg ?? "ffmpeg", args, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup of any partial output on failure.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        if (!File.Exists(tempPath))
        {
            throw new InvalidOperationException("ffmpeg succeeded but output file was not created.");
        }

        try
        {
            File.Move(tempPath, inputPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Often indicates the input is still open by a player/editor.
            // Keep the temp file so the user can recover it.
            throw new InvalidOperationException(
                $"Trim output was created but could not replace the original. Close any apps using the file and try again. Temp file kept: {tempPath}",
                ex);
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!p.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.");
        }

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (p.ExitCode != 0)
        {
            string stderr;
            try { stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false); } catch { stderr = string.Empty; }

            var msg = string.IsNullOrWhiteSpace(stderr)
                ? $"ffmpeg failed (exit code {p.ExitCode})."
                : $"ffmpeg failed (exit code {p.ExitCode}): {stderr.Trim()}";

            throw new InvalidOperationException(msg);
        }
    }
}
