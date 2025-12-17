using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.App.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ScreenRecorder.App.Services.Persona;

internal sealed class WebcamFrameSource : IDisposable, IPersonaFrameSource
{
    private readonly object _gate = new();

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;

    private byte[]? _latest;
    private int _width;
    private int _height;
    private long _version;

    private int _started;

    public bool IsRunning => Interlocked.CompareExchange(ref _started, 1, 1) == 1;

    public async Task<bool> StartAsync(Microsoft.UI.Xaml.Window ownerWindow, CancellationToken ct)
    {
        if (ownerWindow is null) throw new ArgumentNullException(nameof(ownerWindow));

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return true;
        }

        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            capture = new MediaCapture();

            try
            {
                // Some WinRT capture APIs behave better when initialized with a window.
                WindowInterop.InitializeWithWindow(capture, ownerWindow);
            }
            catch { }

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };

            await capture.InitializeAsync(settings).AsTask(ct);

            MediaFrameSource? color = null;
            try
            {
                color = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            }
            catch { }

            if (color is null)
            {
                capture.Dispose();
                capture = null;
                Interlocked.Exchange(ref _started, 0);
                return false;
            }

            // Prefer BGRA8 if supported; else NV12; else whatever the source already supports.
            var formats = color.SupportedFormats;
            var preferred = formats.FirstOrDefault(f => string.Equals(f.Subtype, MediaEncodingSubtypes.Bgra8, StringComparison.OrdinalIgnoreCase))
                         ?? formats.FirstOrDefault(f => string.Equals(f.Subtype, MediaEncodingSubtypes.Nv12, StringComparison.OrdinalIgnoreCase))
                         ?? formats.FirstOrDefault();

            if (preferred is not null)
            {
                try
                {
                    await color.SetFormatAsync(preferred).AsTask(ct);
                }
                catch (Exception fmtEx)
                {
                    Breadcrumbs.Write($"WebcamFrameSource: SetFormatAsync failed subtype='{preferred.Subtype}': {fmtEx.GetType().Name}: {fmtEx.Message}");
                }
            }

            var subtype = preferred?.Subtype;
            Breadcrumbs.Write($"WebcamFrameSource: creating reader subtype='{subtype ?? "(null)"}' supportedFormats={formats.Count}");

            // Create reader. Some devices throw if you request an unsupported subtype.
            // Passing the selected subtype is the most compatible path.
            reader = subtype is not null
                ? await capture.CreateFrameReaderAsync(color, subtype).AsTask(ct)
                : await capture.CreateFrameReaderAsync(color, MediaEncodingSubtypes.Nv12).AsTask(ct);

            reader.FrameArrived += OnFrameArrived;

            var status = await reader.StartAsync().AsTask(ct);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                reader = null;
                capture.Dispose();
                capture = null;
                Interlocked.Exchange(ref _started, 0);
                return false;
            }

            lock (_gate)
            {
                _capture = capture;
                _reader = reader;
                _latest = null;
                _width = 0;
                _height = 0;
                _version = 0;
            }

            Breadcrumbs.Write("WebcamFrameSource: started");
            return true;
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write($"WebcamFrameSource: start failed: {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (reader is not null)
                {
                    reader.FrameArrived -= OnFrameArrived;
                    try { _ = reader.StopAsync(); } catch { }
                    try { reader.Dispose(); } catch { }
                }
            }
            catch { }

            try { capture?.Dispose(); } catch { }
            Interlocked.Exchange(ref _started, 0);
            return false;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        MediaFrameReference? frame = null;
        try
        {
            frame = sender.TryAcquireLatestFrame();
            var vmf = frame?.VideoMediaFrame;
            var sb = vmf?.SoftwareBitmap;
            if (sb is null)
            {
                return;
            }

            // Normalize to BGRA8.
            SoftwareBitmap bgra = sb;
            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                bgra = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8);
            }
            else
            {
                // Clone to detach from the frame lifetime.
                bgra = SoftwareBitmap.Copy(sb);
            }

            try
            {
                var w = bgra.PixelWidth;
                var h = bgra.PixelHeight;
                if (w <= 0 || h <= 0)
                {
                    return;
                }

                var bytes = new byte[w * h * 4];
                bgra.CopyToBuffer(bytes.AsBuffer());

                lock (_gate)
                {
                    _latest = bytes;
                    _width = w;
                    _height = h;
                    _version++;
                }
            }
            finally
            {
                bgra.Dispose();
            }
        }
        catch
        {
            // Ignore frame issues.
        }
        finally
        {
            try { frame?.Dispose(); } catch { }
        }
    }

    public bool TryGetLatest(out byte[]? bgra, out int width, out int height, out long version)
    {
        lock (_gate)
        {
            bgra = _latest;
            width = _width;
            height = _height;
            version = _version;
            return bgra is not null && width > 0 && height > 0;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        MediaFrameReader? reader;
        MediaCapture? capture;

        lock (_gate)
        {
            reader = _reader;
            _reader = null;
            capture = _capture;
            _capture = null;
        }

        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { _ = reader.StopAsync(); } catch { }
                try { reader.Dispose(); } catch { }
            }
        }
        catch { }

        try { capture?.Dispose(); } catch { }

        Breadcrumbs.Write("WebcamFrameSource: stopped");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
