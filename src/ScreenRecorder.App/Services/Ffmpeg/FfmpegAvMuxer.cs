using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using ScreenRecorder.App.Helpers;

namespace ScreenRecorder.App.Services.Ffmpeg;

internal sealed class FfmpegAvMuxer : IDisposable
{
    private readonly object _gate = new();

    private Process? _ffmpeg;
    private Stream? _videoStdin;

    private NamedPipeServerStream? _audioPipe;
    private Task? _stderrTask;
    private Task? _stdoutTask;

    private readonly object _ioLogGate = new();
    private string _lastStdErr = string.Empty;
    private string _lastStdOut = string.Empty;

    public Stream VideoStdin => _videoStdin ?? throw new InvalidOperationException("ffmpeg not started.");
    public Stream AudioPipeStream => _audioPipe ?? throw new InvalidOperationException("ffmpeg not started.");

    public string? PartFilePath { get; private set; }

    public string? LastFinalFilePath { get; private set; }

    public event EventHandler<string>? Finalized;

    public event EventHandler<string>? Status;

    public static bool IsFfmpegAvailable(out string? resolvedPath, out string? message)
    {
        resolvedPath = ResolveFfmpegExecutablePath();

        // If we found an explicit path, we're good.
        if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            Breadcrumbs.Write($"IsFfmpegAvailable: resolvedPath={resolvedPath}");
            message = null;
            return true;
        }

        // Otherwise, probe whether "ffmpeg" can be launched from PATH.
        // This is necessary because a desktop app may not see the same PATH contents as a terminal,
        // and because resolution-by-scanning PATH can miss quoted/expanded entries.
        if (CanLaunchFfmpegFromPath(out var probeError))
        {
            Breadcrumbs.Write("IsFfmpegAvailable: resolved from PATH probe (FileName=ffmpeg)");
            resolvedPath = "ffmpeg";
            message = null;
            return true;
        }

        Breadcrumbs.Write($"IsFfmpegAvailable: failed (probeError={probeError ?? "<null>"})");

        // Note: for MSIX installs, the app install folder is read-only, so "place next to the executable"
        // generally isn't feasible. Prefer PATH or PRISMCAPTURE_FFMPEG, or bundle ffmpeg into the MSIX.
        var help = "Recording requires FFmpeg. Install FFmpeg and ensure `ffmpeg` is on PATH, or set PRISMCAPTURE_FFMPEG to the full path of ffmpeg.exe. For MSIX installs you can also bundle ffmpeg.exe into the package.";
        message = string.IsNullOrWhiteSpace(probeError)
            ? help
            : $"{help} ({probeError})";
        return false;
    }

    private static bool CanLaunchFfmpegFromPath(out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Avoid confusing "working directory System32" errors for MSIX-installed apps.
            try
            {
                var wd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrismCapture");
                Directory.CreateDirectory(wd);
                psi.WorkingDirectory = wd;
            }
            catch
            {
                // best-effort
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                error = "Process.Start returned null";
                return false;
            }

            // Give it a moment; we only need to know it launched.
            if (!p.WaitForExit(1500))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return true;
            }

            // Exit code 0 is ideal, but some builds may return nonzero; treat launch as success.
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Start(
        string outputPartPath,
        int width,
        int height,
        int fps,
        int audioRate = 48000,
        int audioChannels = 2,
        bool useHardwareEncoding = true,
        string? videoFilter = null)
    {
        if (string.IsNullOrWhiteSpace(outputPartPath)) throw new ArgumentException("Output path required.", nameof(outputPartPath));
        if (width <= 0 || height <= 0 || fps <= 0) throw new ArgumentOutOfRangeException("Invalid video parameters.");

        lock (_gate)
        {
            Breadcrumbs.Write("FfmpegAvMuxer.Start: begin");
            Stop_NoLock(finalize: false);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPartPath)!);

            PartFilePath = outputPartPath;

            var encoders = useHardwareEncoding
                ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" }
                : new[] { "libx264" };

            Exception? last = null;
            foreach (var encoder in encoders)
            {
                try
                {
                    Breadcrumbs.Write($"FfmpegAvMuxer.Start: try encoder={encoder}");
                    foreach (var attempt in GetVideoEncoderAttempts(encoder, fps))
                    {
                        try
                        {
                            Breadcrumbs.Write($"FfmpegAvMuxer.Start: try encoder={encoder} attempt={attempt.Name}");
                            StartWithEncoder_NoLock(outputPartPath, width, height, fps, audioRate, audioChannels, encoder, attempt, videoFilter);
                            Status?.Invoke(this, $"Video encoder: {encoder}");
                            Breadcrumbs.Write($"FfmpegAvMuxer.Start: success encoder={encoder} attempt={attempt.Name}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Breadcrumbs.Write($"FfmpegAvMuxer.Start: fail encoder={encoder} attempt={attempt.Name} {ex.GetType().Name}: {ex.Message}");
                            last = ex;
                            DisposeProcessOnly_NoLock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write($"FfmpegAvMuxer.Start: fail encoder={encoder} {ex.GetType().Name}: {ex.Message}");
                    last = ex;
                    DisposeProcessOnly_NoLock();
                }
            }

            Breadcrumbs.Write("FfmpegAvMuxer.Start: failed all encoders");
            throw new InvalidOperationException(
                "ffmpeg failed to start or initialize recording (encoder/pipe setup failed).",
                last);
        }
    }

    private void StartWithEncoder_NoLock(
        string outputPartPath,
        int width,
        int height,
        int fps,
        int audioRate,
        int audioChannels,
        string encoder,
        VideoEncoderAttempt attempt,
        string? videoFilter)
    {
        var audioPipeName = "sr_audio_" + Guid.NewGuid().ToString("N");
        var audioPipePath = $"\\\\.\\pipe\\{audioPipeName}";

        _audioPipe = new NamedPipeServerStream(
            audioPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var args = BuildArgs(outputPartPath, width, height, fps, audioRate, audioChannels, audioPipePath, encoder, attempt, videoFilter);
        Breadcrumbs.Write($"FfmpegAvMuxer: ffmpeg args ({encoder}/{attempt.Name}) = {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveFfmpegExecutablePath() ?? "ffmpeg",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        Breadcrumbs.Write("FfmpegAvMuxer: ffmpeg process started");
        Breadcrumbs.Write($"FfmpegAvMuxer: ffmpeg pid={_ffmpeg.Id}");

        StartIoReaders_NoLock(_ffmpeg);
        _videoStdin = _ffmpeg.StandardInput.BaseStream;

        // Wait for ffmpeg to connect to our audio named pipe.
        var connected = _audioPipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(10));
        if (!connected)
        {
            try
            {
                if (_ffmpeg.HasExited)
                {
                    var msg = GetLastIo_NoLock();
                    var prefix = _ffmpeg.ExitCode != 0
                        ? $"ffmpeg exited (code={_ffmpeg.ExitCode}) before connecting audio pipe."
                        : "ffmpeg exited before connecting audio pipe.";
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? prefix : $"{prefix} {msg}");
                }

                throw new TimeoutException("ffmpeg did not connect to the audio pipe in time.");
            }
            finally
            {
                try { _ffmpeg.Kill(entireProcessTree: true); } catch { }
                try { _ffmpeg.WaitForExit(2000); } catch { }
                try
                {
                    var msg = GetLastIo_NoLock();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        Status?.Invoke(this, $"ffmpeg: {msg.Trim()}");
                    }
                }
                catch
                {
                }
            }
        }

        Breadcrumbs.Write("FfmpegAvMuxer: audio pipe connected");

        // Readers are started immediately after Process.Start to avoid pipe buffer deadlocks.
    }

    private void StartIoReaders_NoLock(Process p)
    {
        try
        {
            var stderr = p.StandardError;
            var stdout = p.StandardOutput;

            // stderr
            _stderrTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var line = await stderr.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }

                        RememberIoLine_NoLock(isErr: true, line);
                        Breadcrumbs.Write($"ffmpeg: {line}");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Normal during shutdown.
                }
                catch (InvalidOperationException)
                {
                    // Normal during shutdown.
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write("FfmpegAvMuxer: stderr reader exception");
                    Breadcrumbs.Write(ex);
                }
            });

            // stdout
            _stdoutTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var line = await stdout.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }

                        RememberIoLine_NoLock(isErr: false, line);
                        Breadcrumbs.Write($"ffmpeg(out): {line}");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Normal during shutdown.
                }
                catch (InvalidOperationException)
                {
                    // Normal during shutdown.
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write("FfmpegAvMuxer: stdout reader exception");
                    Breadcrumbs.Write(ex);
                }
            });
        }
        catch
        {
            // best effort
        }
    }

    private void RememberIoLine_NoLock(bool isErr, string line)
    {
        lock (_ioLogGate)
        {
            if (isErr)
            {
                _lastStdErr = AppendCapped(_lastStdErr, line, 4096);
            }
            else
            {
                _lastStdOut = AppendCapped(_lastStdOut, line, 4096);
            }
        }
    }

    private string GetLastIo_NoLock()
    {
        lock (_ioLogGate)
        {
            var msg = string.Empty;
            if (!string.IsNullOrWhiteSpace(_lastStdErr))
            {
                msg = _lastStdErr;
            }
            else if (!string.IsNullOrWhiteSpace(_lastStdOut))
            {
                msg = _lastStdOut;
            }
            return msg;
        }
    }

    private static string AppendCapped(string existing, string line, int capChars)
    {
        var combined = string.IsNullOrEmpty(existing) ? line : (existing + " | " + line);
        if (combined.Length <= capChars)
        {
            return combined;
        }

        // Keep the tail.
        return combined.Substring(Math.Max(0, combined.Length - capChars));
    }

    private readonly record struct VideoEncoderAttempt(string Name, string[] VideoEncodeArgs);

    private static VideoEncoderAttempt[] GetVideoEncoderAttempts(string encoder, int fps)
    {
        var gop = Math.Max(1, fps * 2);

        if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new VideoEncoderAttempt(
                    Name: "x264_default",
                    VideoEncodeArgs: new[] { "-c:v libx264", "-preset veryfast", "-crf 23", $"-g {gop}" })
            };
        }

        // Hardware encoders: keep options fairly conservative for compatibility.
        // For NVENC specifically, try a couple of presets in case the installed ffmpeg build
        // only supports one naming scheme.
        if (encoder.Equals("h264_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new VideoEncoderAttempt(
                    Name: "nvenc_preset_p5",
                    VideoEncodeArgs: new[] { "-c:v h264_nvenc", "-preset p5", "-profile:v high", "-b:v 8M", "-maxrate 10M", "-bufsize 20M", $"-g {gop}" }),
                new VideoEncoderAttempt(
                    Name: "nvenc_preset_fast",
                    VideoEncodeArgs: new[] { "-c:v h264_nvenc", "-preset fast", "-profile:v high", "-b:v 8M", "-maxrate 10M", "-bufsize 20M", $"-g {gop}" }),
                new VideoEncoderAttempt(
                    Name: "nvenc_minimal",
                    VideoEncodeArgs: new[] { "-c:v h264_nvenc", "-b:v 8M", "-maxrate 10M", "-bufsize 20M", $"-g {gop}" }),
            };
        }

        // Generic hardware encoder attempt.
        return new[]
        {
            new VideoEncoderAttempt(
                Name: "hw_default",
                VideoEncodeArgs: new[] { $"-c:v {encoder}", "-b:v 8M", "-maxrate 10M", "-bufsize 20M", $"-g {gop}" })
        };
    }

    private static string BuildArgs(
        string outputPartPath,
        int width,
        int height,
        int fps,
        int audioRate,
        int audioChannels,
        string audioPipePath,
        string encoder,
        VideoEncoderAttempt attempt,
        string? videoFilter)
    {
        // Note: we intentionally do not force CUDA filter graphs here.
        // NVENC already uses the GPU for encoding; adding CUDA upload/filters can reduce
        // CPU work but is much more dependent on the user's ffmpeg build and installed filters.
        var videoEncodeArgs = attempt.VideoEncodeArgs;

        var parts = new System.Collections.Generic.List<string>
        {
            "-hide_banner",
            "-loglevel error",
            "-y",

            // IMPORTANT: Put the audio pipe input first.
            // Some ffmpeg builds will block while opening pipe:0 (stdin) before they attempt to open
            // the next input, which can deadlock our startup (we wait for audio connection before
            // we start writing video frames).

            // Audio: raw float32 interleaved from named pipe
            "-f f32le",
            $"-ar {audioRate}",
            $"-ac {audioChannels}",
            $"-i \"{audioPipePath}\"",

            // Video: raw BGRA frames from stdin
            "-f rawvideo",
            "-pixel_format bgra",
            $"-video_size {width}x{height}",
            $"-framerate {fps}",
            "-i pipe:0",

            // Explicit mapping since input order is Audio then Video.
            "-map 1:v:0",
            "-map 0:a:0",

            // Optional crop/filter (Region mode)
            string.IsNullOrWhiteSpace(videoFilter) ? string.Empty : $"-vf \"{videoFilter}\"",

            // Encode
            string.Join(" ", videoEncodeArgs),
            "-pix_fmt yuv420p",

            "-c:a aac",
            "-b:a 160k",

            // Crash-friendly MP4 fragmentation
            "-movflags +frag_keyframe+empty_moov+default_base_moof",
            "-flush_packets 1",

            // Output uses a ".mp4.part" suffix, so force the muxer format explicitly.
            "-f mp4",
            $"\"{outputPartPath}\""
        };

        // Remove empty parts (e.g., when no -vf).
        parts.RemoveAll(p => string.IsNullOrWhiteSpace(p));
        return string.Join(" ", parts);
    }

    private static string ReadStdErrQuick(Process p)
    {
        try
        {
            var t = p.StandardError.ReadToEndAsync();
            t.Wait(TimeSpan.FromMilliseconds(250));
            return t.IsCompletedSuccessfully ? t.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Stop(bool finalize)
    {
        lock (_gate)
        {
            Stop_NoLock(finalize);
        }
    }

    private void Stop_NoLock(bool finalize)
    {
        var part = PartFilePath;

        try { _videoStdin?.Flush(); } catch { }
        try { _videoStdin?.Dispose(); } catch { }
        _videoStdin = null;

        try { _audioPipe?.Flush(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        _audioPipe = null;

        try
        {
            if (_ffmpeg is not null && !_ffmpeg.HasExited)
            {
                _ffmpeg.WaitForExit(5000);
            }
        }
        catch { }

        try
        {
            if (_ffmpeg is not null)
            {
                Breadcrumbs.Write($"FfmpegAvMuxer: ffmpeg exited={_ffmpeg.HasExited} code={(_ffmpeg.HasExited ? _ffmpeg.ExitCode : -1)}");
            }
        }
        catch { }

        try { _ffmpeg?.Dispose(); } catch { }
        _ffmpeg = null;

        if (finalize && !string.IsNullOrWhiteSpace(part) && File.Exists(part))
        {
            var finalPath = part.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? part.Substring(0, part.Length - 5)
                : part;

            try
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(part, finalPath);
                LastFinalFilePath = finalPath;
                Status?.Invoke(this, $"Saved: {Path.GetFileName(finalPath)}");
                try
                {
                    Finalized?.Invoke(this, finalPath);
                }
                catch
                {
                    // best-effort
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke(this, $"Finalize failed (kept .part): {ex.Message}");
            }
        }

        PartFilePath = null;
    }

    private void DisposeProcessOnly_NoLock()
    {
        try { _videoStdin?.Dispose(); } catch { }
        _videoStdin = null;

        try { _audioPipe?.Dispose(); } catch { }
        _audioPipe = null;

        try
        {
            if (_ffmpeg is not null && !_ffmpeg.HasExited)
            {
                _ffmpeg.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try { _ffmpeg?.Dispose(); } catch { }
        _ffmpeg = null;
    }

    private static string? ResolveFfmpegExecutablePath()
    {
        try
        {
            // Allow users to ship a local ffmpeg.exe alongside the built app.
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local))
            {
                return local;
            }

            // MSIX builds may preserve folder structure for bundled tools.
            // (The installer bundles under src\ScreenRecorder.App\External\ffmpeg\.)
            var bundled = Path.Combine(AppContext.BaseDirectory, "External", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }
        catch
        {
        }

        // Optional override for debugging/dev.
        try
        {
            var overridePath = Environment.GetEnvironmentVariable("PRISMCAPTURE_FFMPEG")
                ?? Environment.GetEnvironmentVariable("SCREENRECORDER_FFMPEG")
                ?? Environment.GetEnvironmentVariable("FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }
        }
        catch
        {
        }

        // Resolve from PATH explicitly so "availability" checks work.
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (var raw in path.Split(';'))
            {
                var dir = raw?.Trim();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                // Handle quoted PATH segments and environment variables.
                dir = dir.Trim().Trim('"');
                dir = Environment.ExpandEnvironmentVariables(dir);

                string candidate;
                try
                {
                    candidate = Path.Combine(dir, "ffmpeg.exe");
                }
                catch
                {
                    continue;
                }

                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // ignore directories we can't access
                }
            }
        }
        catch
        {
        }

        // Common install locations.
        try
        {
            var candidates = new[]
            {
                @"C:\\ffmpeg\\bin\\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe"),
            };

            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                {
                    return c;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public void Dispose()
    {
        Stop(finalize: false);
        GC.SuppressFinalize(this);
    }
}
