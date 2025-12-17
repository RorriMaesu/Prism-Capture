using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ScreenRecorder.App.Helpers;
using ScreenRecorder.App.Services.Audio;
using ScreenRecorder.App.Services.Annotations;
using ScreenRecorder.App.Services.Compositing;
using ScreenRecorder.App.Services.Ffmpeg;
using ScreenRecorder.App.Services.Persona;
using Windows.Graphics.Capture;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecorder.App.Services;

internal sealed class AvRecorder : IDisposable
{
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _gate = new();
    private readonly object _frameGate = new();

    private readonly WasapiMixedAudioSource _audio;
    private readonly bool _manageAudio;
    private readonly FfmpegAvMuxer _muxer = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winRtDevice;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;

    private VideoCompositor? _compositor;
    private IAnnotationOverlaySource? _annotationSource;
    private long _annotationLastVersion;
    private int _annotationMismatchLogged;

    private IPersonaFrameSource? _personaSource;
    private long _personaLastVersion;
    private ID3D11Texture2D? _personaUpload;
    private int _personaUploadWidth;
    private int _personaUploadHeight;
    private float _personaAudioLevel;
    private Vortice.Mathematics.Color4 _personaBorderColor = new(1, 1, 1, 1);

    private ID3D11Texture2D? _annotationUpload;
    private RectInt32? _crop;
    private int _captureWidth;
    private int _captureHeight;

    private ID3D11Texture2D? _staging;

    private int _width;
    private int _height;
    private int _fps;

    private Channel<byte[]>? _videoChannel;
    private CancellationTokenSource? _cts;
    private Task? _videoWriterTask;
    private Task? _audioWriterTask;

    private long _droppedVideoFrames;
    private int _stopRequested;

    private long _framesReceived;
    private long _lastPerfLogTicks;

    public bool IsRecording { get; private set; }
    public long DroppedVideoFrames => Interlocked.Read(ref _droppedVideoFrames);

    public string? LastFinalFilePath { get; private set; }

    public event EventHandler<string>? RecordingFinalized;

    public event EventHandler<string>? Status;

    public void SetAnnotationOverlaySource(IAnnotationOverlaySource? source)
    {
        lock (_gate)
        {
            _annotationSource = source;

            // If called while recording, synchronize with the frame thread
            // before touching D3D resources.
            lock (_frameGate)
            {
                _annotationLastVersion = 0;
                Interlocked.Exchange(ref _annotationMismatchLogged, 0);

                try { _annotationUpload?.Dispose(); } catch { }
                _annotationUpload = null;
            }
        }
    }

    public void SetPersonaFrameSource(IPersonaFrameSource? source)
    {
        lock (_gate)
        {
            _personaSource = source;

            // If called while recording, synchronize with the frame thread
            // before touching D3D resources.
            lock (_frameGate)
            {
                _personaLastVersion = 0;
                _personaUploadWidth = 0;
                _personaUploadHeight = 0;
                try { _personaUpload?.Dispose(); } catch { }
                _personaUpload = null;
            }
        }
    }

    public void SetPersonaAudioLevel(float level)
    {
        _personaAudioLevel = Math.Clamp(level, 0f, 1f);
    }

    public void SetPersonaBorderColor(Vortice.Mathematics.Color4 c)
    {
        _personaBorderColor = c;
    }

    public AvRecorder(WasapiMixedAudioSource? audio = null, bool manageAudio = true)
    {
        _audio = audio ?? new WasapiMixedAudioSource();
        _manageAudio = manageAudio;

        _muxer.Finalized -= OnMuxerFinalized;
        _muxer.Finalized += OnMuxerFinalized;
    }

    private void OnMuxerFinalized(object? sender, string finalPath)
    {
        LastFinalFilePath = finalPath;
        RecordingFinalized?.Invoke(this, finalPath);
    }

    public void Start(GraphicsCaptureItem item, string outputFolder, int fps = 30, double videoQueueSeconds = 0.5, bool useHardwareEncoding = true, RectInt32? crop = null)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new ArgumentException("Output folder is required.", nameof(outputFolder));

        lock (_gate)
        {
            Breadcrumbs.Write("AvRecorder.Start: begin");
            Stop_NoLock(finalize: false);

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new NotSupportedException("Windows Graphics Capture is not supported on this system.");
            }

            Breadcrumbs.Write("AvRecorder.Start: EnsureDevice");
            EnsureDevice_NoLock();

            _item = item;
            LastFinalFilePath = null;
            _stopRequested = 0;
            _framesReceived = 0;
            _lastPerfLogTicks = 0;
            _fps = fps;
            _captureWidth = Math.Max(1, item.Size.Width);
            _captureHeight = Math.Max(1, item.Size.Height);
            _crop = crop;
            Interlocked.Exchange(ref _annotationMismatchLogged, 0);

            if (crop is not null)
            {
                var r = crop.Value;
                _width = Math.Max(1, r.Width);
                _height = Math.Max(1, r.Height);
            }
            else
            {
                _width = _captureWidth;
                _height = _captureHeight;
            }

            Breadcrumbs.Write($"AvRecorder.Start: capture={_captureWidth}x{_captureHeight} output={_width}x{_height} fps={_fps}");

            Breadcrumbs.Write("AvRecorder.Start: CreateWinRTDevice");
            _winRtDevice = CreateWinRTDeviceFromVorticeDevice(_device!);

            Breadcrumbs.Write("AvRecorder.Start: EnsureStagingTexture");
            EnsureStagingTexture_NoLock(_width, _height);

            _compositor?.Dispose();
            _compositor = new VideoCompositor(_device!, _context!)
            {
                // Step 1 validation: show a red square baked into the output.
                DebugDrawRedSquare = false
            };

            if (_manageAudio)
            {
                Breadcrumbs.Write("AvRecorder.Start: Audio.StartDefault (managed)");
                _audio.StartDefault();
            }
            else if (!_audio.IsRunning)
            {
                Breadcrumbs.Write("AvRecorder.Start: Audio.StartDefault (external)");
                _audio.StartDefault();
            }

            var part = CreatePartFilePath(outputFolder);

            if (_crop is not null)
            {
                var r = _crop.Value;
                Breadcrumbs.Write($"AvRecorder.Start: compositor crop={r.Width}x{r.Height} @ ({r.X},{r.Y})");
            }

            Breadcrumbs.Write($"AvRecorder.Start: muxer start hw={useHardwareEncoding} -> {part}");
            _muxer.Status -= OnMuxerStatus;
            _muxer.Status += OnMuxerStatus;
            // Crop is now handled in the compositor; do not apply ffmpeg crop filters.
            _muxer.Start(part, _width, _height, _fps, useHardwareEncoding: useHardwareEncoding, videoFilter: null);

            Breadcrumbs.Write("AvRecorder.Start: muxer started");

            var cap = Math.Max(2, (int)Math.Ceiling(videoQueueSeconds * fps));
            _videoChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(cap)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

            _cts = new CancellationTokenSource();
            _videoWriterTask = Task.Run(() => VideoWriterLoopAsync(_cts.Token));
            _audioWriterTask = Task.Run(() => AudioWriterLoopAsync(_cts.Token));

            Breadcrumbs.Write("AvRecorder.Start: create framepool");

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            Breadcrumbs.Write("AvRecorder.Start: create session + start capture");
            _session = _framePool.CreateCaptureSession(item);
            _session.StartCapture();

            IsRecording = true;
            Status?.Invoke(this, $"Recording to {Path.GetFileName(part)}");

            Breadcrumbs.Write("AvRecorder.Start: end (IsRecording=true)");
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
        if (!IsRecording)
        {
            CleanupCapture_NoLock();
            if (_manageAudio)
            {
                _audio.Stop();
            }
            _muxer.Stop(finalize: false);
            return;
        }

        IsRecording = false;
        Interlocked.Exchange(ref _stopRequested, 1);

        CleanupCapture_NoLock();

        try { _cts?.Cancel(); } catch { }

        try { _videoWriterTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _audioWriterTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }

        if (_manageAudio)
        {
            _audio.Stop();
        }

        _muxer.Stop(finalize);

        _cts?.Dispose();
        _cts = null;

        _videoWriterTask = null;
        _audioWriterTask = null;

        _videoChannel = null;
        _item = null;

        // Ensure no frame callback is currently reading from staging/context.
        lock (_frameGate)
        {
            _compositor?.Dispose();
            _compositor = null;

            _annotationLastVersion = 0;
            _personaLastVersion = 0;

            try { _annotationUpload?.Dispose(); } catch { }
            _annotationUpload = null;

            try { _personaUpload?.Dispose(); } catch { }
            _personaUpload = null;
            _personaUploadWidth = 0;
            _personaUploadHeight = 0;

            _staging?.Dispose();
            _staging = null;

            _winRtDevice = null;
        }
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

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsRecording)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _stopRequested, 0, 0) != 0)
        {
            return;
        }

        bool requestStop = false;

        lock (_frameGate)
        {
            if (!IsRecording || Interlocked.CompareExchange(ref _stopRequested, 0, 0) != 0)
            {
                return;
            }

            Direct3D11CaptureFrame? frame = null;
            try
            {
                var frames = Interlocked.Increment(ref _framesReceived);
                if (frames == 1)
                {
                    Breadcrumbs.Write("AvRecorder: first frame arrived");
                }

                // Throttle perf logging to ~1x/second.
                var nowTicks = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastPerfLogTicks);
                if (nowTicks - last >= 1000 && Interlocked.CompareExchange(ref _lastPerfLogTicks, nowTicks, last) == last)
                {
                    var dropped = Interlocked.Read(ref _droppedVideoFrames);
                    Breadcrumbs.Write($"AvRecorder: frames={frames} dropped={dropped}");
                }

                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var contentSize = frame.ContentSize;
                if (contentSize.Width != _captureWidth || contentSize.Height != _captureHeight)
                {
                    Status?.Invoke(this, "Capture size changed; stopping recording.");
                    if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
                    {
                        requestStop = true;
                    }
                    return;
                }

                // Capture local references while protected by _frameGate.
                if (_context is null || _staging is null || _compositor is null)
                {
                    return;
                }

                var bytes = CopyCompositedFrameToBgraBytes(frame.Surface, _captureWidth, _captureHeight, _width, _height, _crop);

                if (_videoChannel is null)
                {
                    return;
                }

                if (!_videoChannel.Writer.TryWrite(bytes))
                {
                    Interlocked.Increment(ref _droppedVideoFrames);
                }
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("AvRecorder: OnFrameArrived exception");
                Breadcrumbs.Write(ex);
            }
            finally
            {
                frame?.Dispose();
            }
        }

        if (requestStop)
        {
            _ = Task.Run(() =>
            {
                try { Stop(finalize: true); } catch { }
            });
        }
    }

    private async Task VideoWriterLoopAsync(CancellationToken ct)
    {
        var ch = _videoChannel;
        if (ch is null)
        {
            return;
        }

        var stdin = _muxer.VideoStdin;
        byte[]? last = null;

        var frameBytes = _width * _height * 4;
        var start = System.Diagnostics.Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / _fps);
        var next = interval;

        while (!ct.IsCancellationRequested)
        {
            var now = start.Elapsed;
            if (now < next)
            {
                try { await Task.Delay(next - now, ct).ConfigureAwait(false); }
                catch { break; }
            }
            next += interval;

            if (ch.Reader.TryRead(out var b))
            {
                last = b;
            }

            if (last is null || last.Length != frameBytes)
            {
                continue;
            }

            try
            {
                await stdin.WriteAsync(last, 0, last.Length, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("AvRecorder: video pipe write failed");
                Breadcrumbs.Write(ex);
                Status?.Invoke(this, "ffmpeg video pipe failed.");
                break;
            }
        }
    }

    private async Task AudioWriterLoopAsync(CancellationToken ct)
    {
        var stream = _muxer.AudioPipeStream;

        // 20ms chunks @ 48kHz => 960 frames, stereo => 1920 samples
        const int framesPerChunk = 960;
        const int channels = 2;
        var samples = new float[framesPerChunk * channels];
        var bytes = new byte[samples.Length * sizeof(float)];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _audio.Read(samples, 0, samples.Length);

                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("AvRecorder: audio pipe write failed");
                Breadcrumbs.Write(ex);
                Status?.Invoke(this, "ffmpeg audio pipe failed.");
                break;
            }
        }
    }

    private byte[] CopyFrameToBgraBytes(IDirect3DSurface surface, int width, int height)
    {
        // NOTE: If we hard-crash in native code, breadcrumbs.log should show the last reached step.
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
                System.Runtime.InteropServices.Marshal.Copy(srcRow, data, dstIndex, rowBytes);
                dstIndex += rowBytes;
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return data;
    }

    private byte[] CopyCompositedFrameToBgraBytes(
        IDirect3DSurface surface,
        int captureWidth,
        int captureHeight,
        int outputWidth,
        int outputHeight,
        RectInt32? crop)
    {
        var texturePtr = D3D11SurfaceInterop.GetDxgiInterface(surface, IID_ID3D11Texture2D);
        using var capturedTexture = new ID3D11Texture2D(texturePtr);

        var compositor = _compositor ?? throw new InvalidOperationException("Compositor not initialized.");
        var overlay = TryUpdateAnnotationUpload_NoLock(outputWidth, outputHeight);
        var personaTex = TryUpdatePersonaUpload_NoLock(out var personaW, out var personaH);
        var personaRectUv = ComputePersonaRectUv(outputWidth, outputHeight);
        var (outTex, outW, outH) = compositor.Composite(
            capturedTexture,
            captureWidth,
            captureHeight,
            crop,
            annotationOverlay: overlay,
            annotationWidth: outputWidth,
            annotationHeight: outputHeight,
            personaTexture: personaTex,
            personaWidth: personaW,
            personaHeight: personaH,
            personaRectUv: personaRectUv,
            personaBorderNorm: ComputePersonaBorderNorm(outputWidth, outputHeight),
            personaAudioLevel: _personaAudioLevel,
            timeSeconds: (float)(Environment.TickCount64 / 1000.0),
            personaBorderColor: _personaBorderColor);

        if (outW != outputWidth || outH != outputHeight)
        {
            // Defensive: keep staging + byte sizing consistent.
            outputWidth = outW;
            outputHeight = outH;
        }

        EnsureStagingTexture_NoLock(outputWidth, outputHeight);
        _context!.CopyResource(_staging!, outTex);

        var bytesPerPixel = 4;
        var expectedSize = outputWidth * outputHeight * bytesPerPixel;
        var data = new byte[expectedSize];

        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var srcRowPitch = mapped.RowPitch;
            var dstIndex = 0;
            var rowBytes = outputWidth * bytesPerPixel;
            var srcBase = mapped.DataPointer;

            for (var y = 0; y < outputHeight; y++)
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

    private Vector4? ComputePersonaRectUv(int outputWidth, int outputHeight)
    {
        if (_personaSource is null)
        {
            return null;
        }

        // Bottom-right bubble; size relative to the output.
        var minDim = Math.Max(1, Math.Min(outputWidth, outputHeight));
        var diameter = (int)Math.Round(minDim * 0.22);
        diameter = Math.Clamp(diameter, 160, 320);

        var margin = Math.Clamp((int)Math.Round(minDim * 0.03), 16, 36);

        var x = outputWidth - diameter - margin;
        var y = outputHeight - diameter - margin;

        if (x < 0 || y < 0)
        {
            return null;
        }

        return new Vector4(
            x / (float)outputWidth,
            y / (float)outputHeight,
            diameter / (float)outputWidth,
            diameter / (float)outputHeight);
    }

    private float ComputePersonaBorderNorm(int outputWidth, int outputHeight)
    {
        var minDim = Math.Max(1, Math.Min(outputWidth, outputHeight));
        var diameter = (int)Math.Round(minDim * 0.22);
        diameter = Math.Clamp(diameter, 160, 320);

        // 2px border relative to radius (dist is normalized to radius=1)
        var radiusPx = Math.Max(1f, diameter / 2f);
        return 2f / radiusPx;
    }

    private ID3D11Texture2D? TryUpdatePersonaUpload_NoLock(out int width, out int height)
    {
        width = 0;
        height = 0;

        var src = _personaSource;
        if (src is null)
        {
            return null;
        }

        if (!src.TryGetLatest(out var bytes, out var w, out var h, out var version))
        {
            return null;
        }

        if (bytes is null || w <= 0 || h <= 0)
        {
            return null;
        }

        width = w;
        height = h;

        if (version == _personaLastVersion && _personaUpload is not null && _personaUploadWidth == w && _personaUploadHeight == h)
        {
            return _personaUpload;
        }

        EnsurePersonaUploadTexture_NoLock(w, h);

        var mapped = _context!.Map(_personaUpload!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = w * 4;
            var dstPitch = mapped.RowPitch;
            var dstBase = mapped.DataPointer;

            var srcIndex = 0;
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(bytes, srcIndex, IntPtr.Add(dstBase, y * dstPitch), rowBytes);
                srcIndex += rowBytes;
            }
        }
        finally
        {
            _context.Unmap(_personaUpload!, 0);
        }

        _personaLastVersion = version;
        return _personaUpload;
    }

    private void EnsurePersonaUploadTexture_NoLock(int width, int height)
    {
        if (_personaUpload is not null && _personaUploadWidth == width && _personaUploadHeight == height)
        {
            return;
        }

        try { _personaUpload?.Dispose(); } catch { }
        _personaUpload = null;

        _personaUploadWidth = width;
        _personaUploadHeight = height;

        var td = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None
        };

        _personaUpload = _device!.CreateTexture2D(td);
    }

    private ID3D11Texture2D? TryUpdateAnnotationUpload_NoLock(int width, int height)
    {
        var src = _annotationSource;
        if (src is null)
        {
            return null;
        }

        if (!src.TryGetLatest(out var bytes, out var w, out var h, out var version))
        {
            if (Interlocked.CompareExchange(ref _annotationMismatchLogged, 1, 0) == 0)
            {
                Breadcrumbs.Write("AvRecorder: annotation TryGetLatest=false (no overlay bitmap yet)");
            }
            return null;
        }

        if (bytes is null || w <= 0 || h <= 0)
        {
            if (Interlocked.CompareExchange(ref _annotationMismatchLogged, 1, 0) == 0)
            {
                Breadcrumbs.Write($"AvRecorder: annotation latest invalid bytesNull={(bytes is null)} w={w} h={h}");
            }
            return null;
        }

        if (w != width || h != height)
        {
            // Mismatch: ignore overlay for this frame.
            if (Interlocked.CompareExchange(ref _annotationMismatchLogged, 1, 0) == 0)
            {
                Breadcrumbs.Write($"AvRecorder: annotation size mismatch overlay={w}x{h} expected={width}x{height} (dropping overlay)");
            }
            return null;
        }

        if (version == _annotationLastVersion && _annotationUpload is not null)
        {
            return _annotationUpload;
        }

        EnsureAnnotationUploadTexture_NoLock(width, height);

        var mapped = _context!.Map(_annotationUpload!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            var dstPitch = mapped.RowPitch;
            var dstBase = mapped.DataPointer;

            var srcIndex = 0;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bytes, srcIndex, IntPtr.Add(dstBase, y * dstPitch), rowBytes);
                srcIndex += rowBytes;
            }
        }
        finally
        {
            _context.Unmap(_annotationUpload!, 0);
        }

        _annotationLastVersion = version;
        return _annotationUpload;
    }

    private void EnsureAnnotationUploadTexture_NoLock(int width, int height)
    {
        if (_annotationUpload is not null)
        {
            var d = _annotationUpload.Description;
            if (d.Width == width && d.Height == height)
            {
                return;
            }
        }

        try { _annotationUpload?.Dispose(); } catch { }
        _annotationUpload = null;

        var td = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None
        };

        _annotationUpload = _device!.CreateTexture2D(td);
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
        var name = $"PrismCapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4.part";
        return Path.Combine(outputFolder, name);
    }

    private void OnMuxerStatus(object? sender, string msg) => Status?.Invoke(this, msg);

    private static IDirect3DDevice CreateWinRTDeviceFromVorticeDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevice);
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);

        try
        {
            // CsWinRT expects WinRT objects to be created from their ABI pointers.
            // Transfer ownership of the ABI pointer to the managed wrapper to avoid double-release.
            var d = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
            graphicsDevice = IntPtr.Zero;
            return d;
        }
        finally
        {
            if (graphicsDevice != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.Release(graphicsDevice);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public void Dispose()
    {
        Stop(finalize: false);

        if (_manageAudio)
        {
            _audio.Dispose();
        }
        _muxer.Dispose();

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        GC.SuppressFinalize(this);
    }
}
