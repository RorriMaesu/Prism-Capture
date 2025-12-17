using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ScreenRecorder.App.Helpers;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecorder.App.Services;

internal sealed class CaptureRecorder : IDisposable
{
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winRtDevice;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private ID3D11Texture2D? _staging;

    private int _width;
    private int _height;
    private int _fps;

    private Channel<VideoFrame>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private Process? _ffmpeg;
    private Stream? _ffmpegStdin;

    private long _droppedFrames;

    public bool IsRecording { get; private set; }

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public string? CurrentPartFilePath { get; private set; }

    public event EventHandler<string>? Status;

    public void Start(GraphicsCaptureItem item, string outputFolder, int fps = 30, double queueSeconds = 0.5)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new ArgumentException("Output folder is required.", nameof(outputFolder));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        lock (_gate)
        {
            Stop_NoLock();

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new NotSupportedException("Windows Graphics Capture is not supported on this system.");
            }

            EnsureDevice_NoLock();

            _item = item;
            _fps = fps;
            _width = Math.Max(1, item.Size.Width);
            _height = Math.Max(1, item.Size.Height);

            _winRtDevice = CreateWinRTDeviceFromVorticeDevice(_device!);

            EnsureStagingTexture_NoLock(_width, _height);

            var capacity = Math.Max(2, (int)Math.Ceiling(queueSeconds * fps));
            _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

            CurrentPartFilePath = CreatePartFilePath(outputFolder);
            StartFfmpeg_NoLock(CurrentPartFilePath, _width, _height, _fps);

            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);
            _session.StartCapture();

            IsRecording = true;
            Status?.Invoke(this, $"Recording to {Path.GetFileName(CurrentPartFilePath)}");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            Stop_NoLock();
        }
    }

    private void Stop_NoLock()
    {
        if (!IsRecording)
        {
            CleanupCapture_NoLock();
            CleanupEncoder_NoLock(finalize: false);
            return;
        }

        IsRecording = false;

        CleanupCapture_NoLock();

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _writerTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        CleanupEncoder_NoLock(finalize: true);

        _cts?.Dispose();
        _cts = null;
        _writerTask = null;

        _channel = null;
        _item = null;

        _staging?.Dispose();
        _staging = null;

        _winRtDevice = null;
    }

    private void CleanupCapture_NoLock()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;
    }

    private void CleanupEncoder_NoLock(bool finalize)
    {
        var part = CurrentPartFilePath;

        try
        {
            _ffmpegStdin?.Flush();
        }
        catch
        {
        }

        try
        {
            _ffmpegStdin?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _ffmpegStdin = null;
        }

        try
        {
            if (_ffmpeg is not null && !_ffmpeg.HasExited)
            {
                // Closing stdin should make ffmpeg exit cleanly.
                _ffmpeg.WaitForExit(5000);
            }
        }
        catch
        {
        }

        try
        {
            _ffmpeg?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _ffmpeg = null;
        }

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
                Status?.Invoke(this, $"Saved: {Path.GetFileName(finalPath)}");
            }
            catch (Exception ex)
            {
                Status?.Invoke(this, $"Finalize failed (kept .part): {ex.Message}");
            }
        }

        CurrentPartFilePath = null;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsRecording)
        {
            return;
        }

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width != _width || contentSize.Height != _height)
            {
                // Recording cannot seamlessly handle size changes with rawvideo -> ffmpeg.
                Status?.Invoke(this, "Capture size changed; stopping recording.");
                Stop();
                return;
            }

            var bytes = CopyFrameToBgraBytes(frame.Surface, _width, _height);
            var vf = new VideoFrame(bytes);

            if (_channel is null)
            {
                return;
            }

            if (!_channel.Writer.TryWrite(vf))
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        }
        catch
        {
            // Don't crash the app from capture callbacks.
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var channel = _channel;
        var stdin = _ffmpegStdin;
        if (channel is null || stdin is null)
        {
            return;
        }

        var frameBytes = _width * _height * 4;
        byte[]? last = null;

        var sw = Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
        var nextDue = frameInterval;

        while (!ct.IsCancellationRequested)
        {
            // Pace output as CFR.
            var now = sw.Elapsed;
            if (now < nextDue)
            {
                var delay = nextDue - now;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }

            nextDue += frameInterval;

            if (channel.Reader.TryRead(out var vf))
            {
                last = vf.BgraBytes;
            }

            if (last is null)
            {
                continue;
            }

            if (last.Length != frameBytes)
            {
                continue;
            }

            try
            {
                await stdin.WriteAsync(last, 0, last.Length, ct).ConfigureAwait(false);
            }
            catch
            {
                Status?.Invoke(this, "ffmpeg stopped unexpectedly.");
                break;
            }
        }
    }

    private byte[] CopyFrameToBgraBytes(IDirect3DSurface surface, int width, int height)
    {
        var texturePtr = D3D11SurfaceInterop.GetDxgiInterface(surface, IID_ID3D11Texture2D);
        using var capturedTexture = new ID3D11Texture2D(texturePtr);

        EnsureStagingTexture_NoLock(width, height);

        _context!.CopyResource(_staging!, capturedTexture);

        var bytesPerPixel = 4;
        var expectedSize = width * height * bytesPerPixel;
        var data = new byte[expectedSize];

        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var srcRowPitch = mapped.RowPitch;
            var dstIndex = 0;
            var rowBytes = width * bytesPerPixel;
            var srcBase = mapped.DataPointer;

            for (var y = 0; y < height; y++)
            {
                var srcRow = IntPtr.Add(srcBase, y * srcRowPitch);
                Marshal.Copy(srcRow, data, dstIndex, rowBytes);
                dstIndex += rowBytes;
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return data;
    }

    private void EnsureStagingTexture_NoLock(int width, int height)
    {
        if (_staging is not null && _width == width && _height == height)
        {
            return;
        }

        _staging?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        };

        _staging = _device!.CreateTexture2D(desc);
    }

    private void EnsureDevice_NoLock()
    {
        if (_device is not null && _context is not null)
        {
            return;
        }

        var creationFlags = DeviceCreationFlags.BgraSupport;
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var hr = D3D11.D3D11CreateDevice(
            null!,
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out var device,
            out var context);

        hr.CheckError();
        _device = device;
        _context = context;
    }

    private static string CreatePartFilePath(string outputFolder)
    {
        var name = $"PrismCapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mkv.part";
        return Path.Combine(outputFolder, name);
    }

    private void StartFfmpeg_NoLock(string partFilePath, int width, int height, int fps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partFilePath)!);

        // Minimal encoder: raw BGRA frames -> libx264 -> MKV
        // Requires ffmpeg available on PATH.
        var args = string.Join(" ", new[]
        {
            "-hide_banner",
            "-loglevel error",
            "-y",
            "-f rawvideo",
            "-pixel_format bgra",
            $"-video_size {width}x{height}",
            $"-framerate {fps}",
            "-i pipe:0",
            "-c:v libx264",
            "-preset veryfast",
            "-crf 23",
            "-pix_fmt yuv420p",
            "-f matroska",
            "-flush_packets 1",
            $"\"{partFilePath}\""
        });

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _ffmpeg = Process.Start(psi);
            if (_ffmpeg is null)
            {
                throw new InvalidOperationException("Failed to start ffmpeg.");
            }

            _ffmpegStdin = _ffmpeg.StandardInput.BaseStream;

            // Drain stderr to avoid deadlocks.
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await _ffmpeg.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        Status?.Invoke(this, $"ffmpeg: {stderr.Trim()}" );
                    }
                }
                catch
                {
                }
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("ffmpeg not found or failed to start. Install ffmpeg and ensure it's on PATH.", ex);
        }
    }

    private static IDirect3DDevice CreateWinRTDeviceFromVorticeDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevice);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var obj = Marshal.GetObjectForIUnknown(graphicsDevice);
            if (obj is not IDirect3DDevice d)
            {
                throw new InvalidOperationException("Failed to create WinRT IDirect3DDevice.");
            }

            return d;
        }
        finally
        {
            Marshal.Release(graphicsDevice);
        }
    }

    [System.Runtime.InteropServices.DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private readonly record struct VideoFrame(byte[] BgraBytes);

    public void Dispose()
    {
        Stop();

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        GC.SuppressFinalize(this);
    }
}
