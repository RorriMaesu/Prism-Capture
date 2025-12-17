using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using ScreenRecorder.App.Helpers;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

// Vortice wrappers (avoids hand-rolled COM for D3D11/DXGI)
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;

namespace ScreenRecorder.App.Services;

internal sealed class CapturePreview : IDisposable
{
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly SwapChainPanel _panel;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;

    private ID3D11Texture2D? _sampleTexture;
    private ID3D11ShaderResourceView? _sampleSrv;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _winRtDevice;

    private int _width;
    private int _height;

    private int _panelPixelWidth;
    private int _panelPixelHeight;

    private double _rasterizationScale = 1.0;

    private int _isStarted;

    private int _renderErrorLogged;
    private int _previewFrameCount;

    public CapturePreview(SwapChainPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));

        _panel.SizeChanged -= OnPanelSizeChanged;
        _panel.SizeChanged += OnPanelSizeChanged;
    }

    private void OnPanelSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isStarted, 1, 1) != 1)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                UpdatePanelPixelSizeFromUi_NoLock(e.NewSize.Width, e.NewSize.Height);

                if (_panelPixelWidth > 0 && _panelPixelHeight > 0)
                {
                    Breadcrumbs.Write($"CapturePreview: resize panelPx={_panelPixelWidth}x{_panelPixelHeight} scale={_rasterizationScale:0.###}");
                    EnsureSwapChain_NoLock(_panelPixelWidth, _panelPixelHeight);
                }
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _renderErrorLogged, 1) == 0)
            {
                Breadcrumbs.Write("CapturePreview: resize failed BEGIN");
                Breadcrumbs.Write(ex.ToString());
                Breadcrumbs.Write("CapturePreview: resize failed END");
            }
        }
    }

    public void Start(GraphicsCaptureItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            Stop_NoLock();

            EnsureDevice_NoLock();

            _item = item;
            _width = Math.Max(1, item.Size.Width);
            _height = Math.Max(1, item.Size.Height);

            _renderErrorLogged = 0;
            _previewFrameCount = 0;

            EnsureShaders_NoLock();

            // IMPORTANT: panel sizing + XamlRoot access must happen on the UI thread.
            // Start is called from MainPage (UI thread), so cache pixel sizing here.
            UpdatePanelPixelSizeFromUi_NoLock(_panel.ActualWidth, _panel.ActualHeight);
            if (_panelPixelWidth <= 0 || _panelPixelHeight <= 0)
            {
                // Layout may not be ready yet; create a temporary swapchain so we can start rendering.
                // SizeChanged will resize it once the panel has a real size.
                _panelPixelWidth = Math.Max(1, _width);
                _panelPixelHeight = Math.Max(1, _height);
            }

            Breadcrumbs.Write($"CapturePreview.Start: capture={_width}x{_height} panelPx={_panelPixelWidth}x{_panelPixelHeight} scale={_rasterizationScale:0.###}");

            EnsureSwapChain_NoLock(_panelPixelWidth, _panelPixelHeight);

            _winRtDevice = CreateWinRTDeviceFromVorticeDevice(_device!);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);
            // Some session properties are gated by TargetPlatformMinVersion.
            // Keep preview minimal and compatible.

            _session.StartCapture();
            Interlocked.Exchange(ref _isStarted, 1);
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
        Interlocked.Exchange(ref _isStarted, 0);

        try
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
        }
        catch { }

        try { _session?.Dispose(); } catch { }
        _session = null;

        try { _framePool?.Dispose(); } catch { }
        _framePool = null;

        _winRtDevice = null;

        _item = null;

        try { _sampleSrv?.Dispose(); } catch { }
        _sampleSrv = null;

        try { _sampleTexture?.Dispose(); } catch { }
        _sampleTexture = null;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Interlocked.CompareExchange(ref _isStarted, 1, 1) != 1)
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

            var fc = Interlocked.Increment(ref _previewFrameCount);
            if (fc == 1)
            {
                Breadcrumbs.Write("CapturePreview: first frame arrived");
            }
            else if (fc % 300 == 0)
            {
                Breadcrumbs.Write($"CapturePreview: frames={fc}");
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width != _width || contentSize.Height != _height)
            {
                lock (_gate)
                {
                    if (_item is not null)
                    {
                        _width = Math.Max(1, contentSize.Width);
                        _height = Math.Max(1, contentSize.Height);

                        // Swapchain resize is driven by UI thread SizeChanged.
                        // If we don't yet have a valid panel size, keep using the existing swapchain.
                        if (_panelPixelWidth > 0 && _panelPixelHeight > 0)
                        {
                            EnsureSwapChain_NoLock(_panelPixelWidth, _panelPixelHeight);
                        }

                        if (_winRtDevice is not null)
                        {
                            _framePool?.Recreate(
                                _winRtDevice,
                                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                2,
                                contentSize);
                        }
                    }
                }
            }

            // Render captured texture into the swapchain backbuffer with aspect-preserving scaling.
            var surface = frame.Surface;
            var texturePtr = D3D11SurfaceInterop.GetDxgiInterface(surface, IID_ID3D11Texture2D);
            using var capturedTexture = new ID3D11Texture2D(texturePtr);

            EnsureSampleTexture_NoLock(_width, _height);
            _context!.CopyResource(_sampleTexture!, capturedTexture);

            _context!.OMSetRenderTargets(_rtv!);
            _context.ClearRenderTargetView(_rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 1));

            // Letterbox/pillarbox to preserve aspect.
            var bbW = Math.Max(1, _panelPixelWidth);
            var bbH = Math.Max(1, _panelPixelHeight);
            var captureAspect = _width / (float)_height;
            var bbAspect = bbW / (float)bbH;

            int vpX, vpY, vpW, vpH;
            if (bbAspect > captureAspect)
            {
                // Pillarbox
                vpH = bbH;
                vpW = Math.Max(1, (int)Math.Round(bbH * captureAspect));
                vpX = (bbW - vpW) / 2;
                vpY = 0;
            }
            else
            {
                // Letterbox
                vpW = bbW;
                vpH = Math.Max(1, (int)Math.Round(bbW / captureAspect));
                vpX = 0;
                vpY = (bbH - vpH) / 2;
            }

            _context.RSSetViewport(new Viewport(vpX, vpY, vpW, vpH, 0, 1));

            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs!);
            _context.PSSetShader(_ps!);
            _context.PSSetSampler(0, _sampler!);
            _context.PSSetShaderResource(0, _sampleSrv!);

            _context.Draw(3, 0);

            // Unbind to allow reuse/release.
            _context.PSSetShaderResource(0, null!);

            _swapChain!.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _renderErrorLogged, 1) == 0)
            {
                Breadcrumbs.Write("CapturePreview: frame render failed BEGIN");
                Breadcrumbs.Write(ex.ToString());
                Breadcrumbs.Write("CapturePreview: frame render failed END");
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void EnsureSampleTexture_NoLock(int width, int height)
    {
        if (_sampleTexture is not null && _sampleSrv is not null)
        {
            var desc = _sampleTexture.Description;
            if (desc.Width == width && desc.Height == height)
            {
                return;
            }

            try { _sampleSrv.Dispose(); } catch { }
            _sampleSrv = null;

            try { _sampleTexture.Dispose(); } catch { }
            _sampleTexture = null;
        }

        var td = new Texture2DDescription
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        _sampleTexture = _device!.CreateTexture2D(td);
        _sampleSrv = _device.CreateShaderResourceView(_sampleTexture);
    }

    private void EnsureDevice_NoLock()
    {
        if (_device is not null && _context is not null)
        {
            return;
        }

        // Create a D3D11 device suitable for composition + capture.
        var creationFlags = DeviceCreationFlags.BgraSupport;
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };

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

        EnsureShaders_NoLock();
    }

    private void EnsureShaders_NoLock()
    {
        if (_vs is not null && _ps is not null && _sampler is not null)
        {
            return;
        }

        const string hlsl = @"
struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    float2 pos[3] = { float2(-1, -1), float2(-1, 3), float2(3, -1) };
    float2 uv[3]  = { float2(0, 1),  float2(0, -1), float2(2, 1) };
    VSOut o;
    o.pos = float4(pos[id], 0, 1);
    o.uv  = uv[id];
    return o;
}

Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);

float4 PS(VSOut i) : SV_Target
{
    return tex0.Sample(samp0, i.uv);
}
";

        var src = Encoding.UTF8.GetBytes(hlsl);
        unsafe
        {
            fixed (byte* p = src)
            {
                Blob vsBlob;
                Blob vsErr;
                var r1 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "CapturePreview.hlsl",
                    null,
                    null,
                    "VS",
                    "vs_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out vsBlob,
                    out vsErr);
                r1.CheckError();
                try { vsErr?.Dispose(); } catch { }

                Blob psBlob;
                Blob psErr;
                var r2 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "CapturePreview.hlsl",
                    null,
                    null,
                    "PS",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psBlob,
                    out psErr);
                r2.CheckError();
                try { psErr?.Dispose(); } catch { }

                try
                {
                    _vs = _device!.CreateVertexShader(vsBlob);
                    _ps = _device!.CreatePixelShader(psBlob);
                }
                finally
                {
                    try { vsBlob.Dispose(); } catch { }
                    try { psBlob.Dispose(); } catch { }
                }
            }
        }

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });
    }

    private (int width, int height) GetPanelPixelSize()
    {
        // NOTE: do not call this from a non-UI thread (XamlRoot access will fail).
        UpdatePanelPixelSizeFromUi_NoLock(_panel.ActualWidth, _panel.ActualHeight);
        return (_panelPixelWidth, _panelPixelHeight);
    }

    private void UpdatePanelPixelSizeFromUi_NoLock(double dipWidth, double dipHeight)
    {
        // Only safe on UI thread.
        try
        {
            _rasterizationScale = _panel.XamlRoot?.RasterizationScale ?? 1.0;
        }
        catch
        {
            // If XamlRoot isn't available yet, keep last known scale.
        }

        var w = (int)Math.Round(Math.Max(0, dipWidth) * _rasterizationScale);
        var h = (int)Math.Round(Math.Max(0, dipHeight) * _rasterizationScale);
        _panelPixelWidth = Math.Max(1, w);
        _panelPixelHeight = Math.Max(1, h);
    }

    private void EnsureSwapChain_NoLock(int width, int height)
    {
        if (_swapChain is not null)
        {
            // Resize buffers if needed.
            _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);

            try { _rtv?.Dispose(); } catch { }
            _rtv = null;
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device!.CreateRenderTargetView(backBuffer);
            return;
        }

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        if (adapter is null)
        {
            throw new InvalidOperationException("Failed to resolve DXGI adapter.");
        }

        using var factory = adapter.GetParent<IDXGIFactory2>();
        if (factory is null)
        {
            throw new InvalidOperationException("Failed to resolve DXGI factory.");
        }

        var desc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            Usage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore
        };

        _swapChain = factory.CreateSwapChainForComposition(_device!, desc);

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            _rtv = _device!.CreateRenderTargetView(backBuffer);
        }

        // Hook swap chain to SwapChainPanel
        var unk = Marshal.GetIUnknownForObject(_panel);
        try
        {
            var iid = typeof(ISwapChainPanelNative).GUID;
            Marshal.QueryInterface(unk, ref iid, out var panelNativePtr);
            if (panelNativePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("SwapChainPanel does not support ISwapChainPanelNative.");
            }

            try
            {
                var obj = Marshal.GetObjectForIUnknown(panelNativePtr);
                if (obj is not ISwapChainPanelNative panelNative)
                {
                    throw new InvalidOperationException("Failed to acquire ISwapChainPanelNative.");
                }

                panelNative.SetSwapChain(_swapChain.NativePointer);
            }
            finally
            {
                Marshal.Release(panelNativePtr);
            }
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    private static IDirect3DDevice CreateWinRTDeviceFromVorticeDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevice);
        Marshal.ThrowExceptionForHR(hr);

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
                Marshal.Release(graphicsDevice);
            }
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public void Dispose()
    {
        Stop();

        try { _panel.SizeChanged -= OnPanelSizeChanged; } catch { }

        try { _sampleSrv?.Dispose(); } catch { }
        _sampleSrv = null;

        try { _sampleTexture?.Dispose(); } catch { }
        _sampleTexture = null;

        try { _rtv?.Dispose(); } catch { }
        _rtv = null;

        try { _sampler?.Dispose(); } catch { }
        _sampler = null;

        try { _ps?.Dispose(); } catch { }
        _ps = null;

        try { _vs?.Dispose(); } catch { }
        _vs = null;

        _swapChain?.Dispose();
        _swapChain = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;

        GC.SuppressFinalize(this);
    }

    [ComImport]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        void SetSwapChain(IntPtr swapChain);
    }
}
