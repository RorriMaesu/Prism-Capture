using System;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenRecorder.App.Services.Compositing;

internal sealed class VideoCompositor : IDisposable
{
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psSample;
    private ID3D11PixelShader? _psSampleWithOverlay;
    private ID3D11PixelShader? _psSampleWithPersona;
    private ID3D11PixelShader? _psSampleWithOverlayAndPersona;
    private ID3D11PixelShader? _psSolid;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _paramsCbuffer;

    private ID3D11Texture2D? _sampleTexture;
    private ID3D11ShaderResourceView? _sampleSrv;

    private ID3D11Texture2D? _overlaySource;
    private int _overlayWidth;
    private int _overlayHeight;
    private ID3D11Texture2D? _overlaySrvSource;
    private ID3D11ShaderResourceView? _overlaySrv;

    private ID3D11Texture2D? _personaSource;
    private int _personaWidth;
    private int _personaHeight;
    private ID3D11Texture2D? _personaSrvSource;
    private ID3D11ShaderResourceView? _personaSrv;

    private ID3D11Texture2D? _outputTexture;
    private ID3D11RenderTargetView? _outputRtv;

    private int _captureWidth;
    private int _captureHeight;
    private int _outputWidth;
    private int _outputHeight;

    public bool DebugDrawRedSquare { get; set; } = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector4 UvTransform;
        public Vector4 PersonaRect;   // (x,y,w,h) in output UV space
        public Vector4 PersonaParams; // (enabled, borderNorm, audioLevel, timeSeconds)
        public Vector4 PersonaColor;  // (r,g,b,a)
    }

    public VideoCompositor(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public (ID3D11Texture2D OutputTexture, int OutputWidth, int OutputHeight) Composite(
        ID3D11Texture2D capturedTexture,
        int captureWidth,
        int captureHeight,
        Windows.Graphics.RectInt32? crop,
        ID3D11Texture2D? annotationOverlay = null,
        int annotationWidth = 0,
        int annotationHeight = 0,
        ID3D11Texture2D? personaTexture = null,
        int personaWidth = 0,
        int personaHeight = 0,
        Vector4? personaRectUv = null,
        float personaBorderNorm = 0.01f,
        float personaAudioLevel = 0f,
        float timeSeconds = 0f,
        Color4? personaBorderColor = null)
    {
        if (capturedTexture is null) throw new ArgumentNullException(nameof(capturedTexture));
        captureWidth = Math.Max(1, captureWidth);
        captureHeight = Math.Max(1, captureHeight);

        int outW;
        int outH;
        float scaleX;
        float scaleY;
        float offsetX;
        float offsetY;

        if (crop is not null)
        {
            var r = crop.Value;
            outW = Math.Max(1, r.Width);
            outH = Math.Max(1, r.Height);

            // Normalize crop coordinates to [0,1] texture UV space.
            scaleX = outW / (float)captureWidth;
            scaleY = outH / (float)captureHeight;
            offsetX = r.X / (float)captureWidth;
            offsetY = r.Y / (float)captureHeight;
        }
        else
        {
            outW = captureWidth;
            outH = captureHeight;
            scaleX = 1f;
            scaleY = 1f;
            offsetX = 0f;
            offsetY = 0f;
        }

        EnsureShaders_NoLock();
        EnsureConstantBuffer_NoLock();
        EnsureSampleTexture_NoLock(captureWidth, captureHeight);
        EnsureOutputTexture_NoLock(outW, outH);

        if (annotationOverlay is not null && annotationWidth > 0 && annotationHeight > 0)
        {
            _overlaySource = annotationOverlay;
            _overlayWidth = annotationWidth;
            _overlayHeight = annotationHeight;
        }
        else
        {
            _overlaySource = null;
            _overlayWidth = 0;
            _overlayHeight = 0;
        }

        if (personaTexture is not null && personaWidth > 0 && personaHeight > 0)
        {
            _personaSource = personaTexture;
            _personaWidth = personaWidth;
            _personaHeight = personaHeight;
        }
        else
        {
            _personaSource = null;
            _personaWidth = 0;
            _personaHeight = 0;
        }

        // Copy captured -> sample texture (SRV-compatible)
        _context.CopyResource(_sampleTexture!, capturedTexture);

        // Update constants
        var uvTransform = new Vector4(scaleX, scaleY, offsetX, offsetY);
        var pRect = personaRectUv ?? Vector4.Zero;
        var enabled = (_personaSource is not null && _personaWidth > 0 && _personaHeight > 0 && pRect.Z > 0 && pRect.W > 0) ? 1f : 0f;
        var pParams = new Vector4(enabled, personaBorderNorm, Math.Clamp(personaAudioLevel, 0f, 1f), timeSeconds);
        var c = personaBorderColor ?? new Color4(1, 1, 1, 1);
        var pColor = new Vector4(c.R, c.G, c.B, c.A);
        UpdateParams_NoLock(new Params
        {
            UvTransform = uvTransform,
            PersonaRect = pRect,
            PersonaParams = pParams,
            PersonaColor = pColor
        });

        // Base pass: sample captured
        _context.OMSetRenderTargets(_outputRtv!);
        _context.ClearRenderTargetView(_outputRtv!, new Color4(0, 0, 0, 1));

        _context.RSSetViewport(new Viewport(0, 0, outW, outH, 0, 1));

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs!);
        var hasOverlay = _overlaySource is not null && _overlayWidth == outW && _overlayHeight == outH;
        var hasPersona = _personaSource is not null && _personaWidth > 0 && _personaHeight > 0;

        if (hasOverlay && hasPersona)
        {
            _context.PSSetShader(_psSampleWithOverlayAndPersona!);
        }
        else if (hasOverlay)
        {
            _context.PSSetShader(_psSampleWithOverlay!);
        }
        else if (hasPersona)
        {
            _context.PSSetShader(_psSampleWithPersona!);
        }
        else
        {
            _context.PSSetShader(_psSample!);
        }
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, _sampleSrv!);

        if (hasOverlay)
        {
            EnsureOverlaySrv_NoLock(_overlaySource!);
            _context.PSSetShaderResource(1, _overlaySrv!);
        }

        if (hasPersona)
        {
            EnsurePersonaSrv_NoLock(_personaSource!);
            _context.PSSetShaderResource(2, _personaSrv!);
        }

        _context.PSSetConstantBuffer(0, _paramsCbuffer!);

        _context.Draw(3, 0);

        // Unbind SRV to avoid D3D warning on reuse.
        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.PSSetShaderResource(2, null!);

        if (DebugDrawRedSquare)
        {
            // Debug overlay pass: draw a red square in output.
            var size = Math.Max(24, Math.Min(outW, outH) / 8);
            var margin = Math.Max(16, size / 4);

            _context.RSSetViewport(new Viewport(margin, margin, size, size, 0, 1));
            _context.VSSetShader(_vs!);
            _context.PSSetShader(_psSolid!);
            _context.Draw(3, 0);
        }

        _captureWidth = captureWidth;
        _captureHeight = captureHeight;
        _outputWidth = outW;
        _outputHeight = outH;

        return (_outputTexture!, outW, outH);
    }

    private void EnsureSampleTexture_NoLock(int width, int height)
    {
        if (_sampleTexture is not null && _sampleSrv is not null)
        {
            var d = _sampleTexture.Description;
            if (d.Width == width && d.Height == height)
            {
                return;
            }
        }

        _sampleSrv?.Dispose();
        _sampleSrv = null;

        _sampleTexture?.Dispose();
        _sampleTexture = null;

        var td = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        _sampleTexture = _device.CreateTexture2D(td);
        _sampleSrv = _device.CreateShaderResourceView(_sampleTexture);
    }

    private void EnsureOverlaySrv_NoLock(ID3D11Texture2D source)
    {
        if (_overlaySrv is not null && _overlaySrvSource == source)
        {
            return;
        }

        _overlaySrv?.Dispose();
        _overlaySrv = null;
        _overlaySrvSource = source;

        _overlaySrv = _device.CreateShaderResourceView(source);
    }

    private void EnsurePersonaSrv_NoLock(ID3D11Texture2D source)
    {
        if (_personaSrv is not null && _personaSrvSource == source)
        {
            return;
        }

        _personaSrv?.Dispose();
        _personaSrv = null;
        _personaSrvSource = source;

        _personaSrv = _device.CreateShaderResourceView(source);
    }

    private void EnsureOutputTexture_NoLock(int width, int height)
    {
        if (_outputTexture is not null && _outputRtv is not null)
        {
            var d = _outputTexture.Description;
            if (d.Width == width && d.Height == height)
            {
                return;
            }
        }

        _outputRtv?.Dispose();
        _outputRtv = null;

        _outputTexture?.Dispose();
        _outputTexture = null;

        var td = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        _outputTexture = _device.CreateTexture2D(td);
        _outputRtv = _device.CreateRenderTargetView(_outputTexture);
    }

    private void EnsureConstantBuffer_NoLock()
    {
        if (_paramsCbuffer is not null)
        {
            return;
        }

        // b0: Params (multiple float4s)
        var bd = new BufferDescription
        {
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Dynamic,
            CpuAccessFlags = CpuAccessFlags.Write,
            SizeInBytes = Marshal.SizeOf<Params>(),
        };

        _paramsCbuffer = _device.CreateBuffer(bd);
    }

    private unsafe void UpdateParams_NoLock(in Params p)
    {
        var mapped = _context.Map(_paramsCbuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(Params*)mapped.DataPointer = p;
        }
        finally
        {
            _context.Unmap(_paramsCbuffer!, 0);
        }
    }

    private void EnsureShaders_NoLock()
    {
        if (_vs is not null && _psSample is not null && _psSampleWithOverlay is not null && _psSampleWithPersona is not null && _psSampleWithOverlayAndPersona is not null && _psSolid is not null && _sampler is not null)
        {
            return;
        }

        const string hlsl = @"
cbuffer Params : register(b0)
{
    float4 uvTransform;   // (scaleX, scaleY, offsetX, offsetY)
    float4 personaRect;   // (x,y,w,h) in output UV space
    float4 personaParams; // (enabled, borderNorm, audioLevel, timeSeconds)
    float4 personaColor;  // (r,g,b,a)
};

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
Texture2D tex1 : register(t1);
Texture2D tex2 : register(t2);
SamplerState samp0 : register(s0);

float4 PSSample(VSOut i) : SV_Target
{
    float2 uv = i.uv * uvTransform.xy + uvTransform.zw;
    return tex0.Sample(samp0, uv);
}

float4 PSSampleOverlay(VSOut i) : SV_Target
{
    float2 uv = i.uv * uvTransform.xy + uvTransform.zw;
    float4 base = tex0.Sample(samp0, uv);
    float4 over = tex1.Sample(samp0, i.uv);
    // Overlay input is BGRA premultiplied-alpha (same bytes we use for UpdateLayeredWindow).
    // For premultiplied alpha, the correct blend is: out = over + base * (1 - over.a)
    float3 rgb = over.rgb + base.rgb * (1.0 - over.a);
    return float4(rgb, 1.0);
}

float4 ApplyPersona(float3 baseRgb, float2 outUv) 
{
    if (personaParams.x < 0.5)
    {
        return float4(baseRgb, 1.0);
    }

    float2 uv = saturate(outUv);
    float2 local = (uv - personaRect.xy) / max(personaRect.zw, float2(1e-6, 1e-6));

    // Limit to the rect
    if (local.x < 0 || local.x > 1 || local.y < 0 || local.y > 1)
    {
        return float4(baseRgb, 1.0);
    }

    float2 p = local - float2(0.5, 0.5);
    float dist = length(p) * 2.0; // 0 center, 1 edge

    // Anti-aliased circle mask
    float feather = 0.014;
    float mask = 1.0 - smoothstep(1.0 - feather, 1.0 + feather, dist);

    // Sample camera; flip Y to match typical BGRA image orientation
    float2 camUv = float2(local.x, 1.0 - local.y);
    float3 camRgb = tex2.Sample(samp0, camUv).rgb;

    float3 rgb = camRgb * mask + baseRgb * (1.0 - mask);

    // Border (thin, elegant)
    float border = max(0.0005, personaParams.y);
    float borderOuter = 1.0;
    float borderInner = 1.0 - border;
    float borderBand = (1.0 - smoothstep(borderOuter - feather, borderOuter + feather, dist))
                     * smoothstep(borderInner - feather, borderInner + feather, dist);
    rgb = personaColor.rgb * borderBand + rgb * (1.0 - borderBand);

    // Audio-reactive ring pulse (subtle translucent)
    float audio = saturate(personaParams.z);
    float t = personaParams.w;
    float pulse = frac(t * 1.6);
    float ringR = 1.0 + pulse * (0.42 * audio);
    float ringW = border * 2.2;
    float ringBand = smoothstep(ringR + ringW, ringR, dist) * smoothstep(ringR - ringW, ringR, dist);
    float ringAlpha = ringBand * audio * (1.0 - pulse) * 0.35;
    rgb = personaColor.rgb * ringAlpha + rgb * (1.0 - ringAlpha);

    return float4(rgb, 1.0);
}

float4 PSSamplePersona(VSOut i) : SV_Target
{
    float2 uv = i.uv * uvTransform.xy + uvTransform.zw;
    float4 base = tex0.Sample(samp0, uv);
    return ApplyPersona(base.rgb, i.uv);
}

float4 PSSampleOverlayPersona(VSOut i) : SV_Target
{
    float2 uv = i.uv * uvTransform.xy + uvTransform.zw;
    float4 base = tex0.Sample(samp0, uv);
    float4 over = tex1.Sample(samp0, i.uv);
    float3 rgb = over.rgb + base.rgb * (1.0 - over.a);
    return ApplyPersona(rgb, i.uv);
}

float4 PSSolid(VSOut i) : SV_Target
{
    return float4(1, 0, 0, 1);
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
                    "VideoCompositor.hlsl",
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

                Blob psSampleBlob;
                Blob psSampleErr;
                var r2 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "VideoCompositor.hlsl",
                    null,
                    null,
                    "PSSample",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psSampleBlob,
                    out psSampleErr);
                r2.CheckError();
                try { psSampleErr?.Dispose(); } catch { }

                Blob psOverlayBlob;
                Blob psOverlayErr;
                var r2b = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "VideoCompositor.hlsl",
                    null,
                    null,
                    "PSSampleOverlay",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psOverlayBlob,
                    out psOverlayErr);
                r2b.CheckError();
                try { psOverlayErr?.Dispose(); } catch { }

                Blob psSolidBlob;
                Blob psSolidErr;
                var r3 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "VideoCompositor.hlsl",
                    null,
                    null,
                    "PSSolid",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psSolidBlob,
                    out psSolidErr);
                r3.CheckError();
                try { psSolidErr?.Dispose(); } catch { }

                Blob psPersonaBlob;
                Blob psPersonaErr;
                var r4 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "VideoCompositor.hlsl",
                    null,
                    null,
                    "PSSamplePersona",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psPersonaBlob,
                    out psPersonaErr);
                r4.CheckError();
                try { psPersonaErr?.Dispose(); } catch { }

                Blob psOverlayPersonaBlob;
                Blob psOverlayPersonaErr;
                var r5 = Compiler.Compile(
                    (IntPtr)p,
                    new PointerSize(src.Length),
                    "VideoCompositor.hlsl",
                    null,
                    null,
                    "PSSampleOverlayPersona",
                    "ps_5_0",
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None,
                    out psOverlayPersonaBlob,
                    out psOverlayPersonaErr);
                r5.CheckError();
                try { psOverlayPersonaErr?.Dispose(); } catch { }

                try
                {
                    _vs = _device.CreateVertexShader(vsBlob);
                    _psSample = _device.CreatePixelShader(psSampleBlob);
                    _psSampleWithOverlay = _device.CreatePixelShader(psOverlayBlob);
                    _psSampleWithPersona = _device.CreatePixelShader(psPersonaBlob);
                    _psSampleWithOverlayAndPersona = _device.CreatePixelShader(psOverlayPersonaBlob);
                    _psSolid = _device.CreatePixelShader(psSolidBlob);
                }
                finally
                {
                    try { vsBlob.Dispose(); } catch { }
                    try { psSampleBlob.Dispose(); } catch { }
                    try { psOverlayBlob.Dispose(); } catch { }
                    try { psPersonaBlob.Dispose(); } catch { }
                    try { psOverlayPersonaBlob.Dispose(); } catch { }
                    try { psSolidBlob.Dispose(); } catch { }
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

    public void Dispose()
    {
        _overlaySrv?.Dispose();
        _overlaySrv = null;

        _overlaySrvSource = null;

        _personaSrv?.Dispose();
        _personaSrv = null;

        _personaSrvSource = null;

        _sampleSrv?.Dispose();
        _sampleSrv = null;

        _sampleTexture?.Dispose();
        _sampleTexture = null;

        _outputRtv?.Dispose();
        _outputRtv = null;

        _outputTexture?.Dispose();
        _outputTexture = null;

        _paramsCbuffer?.Dispose();
        _paramsCbuffer = null;

        _sampler?.Dispose();
        _sampler = null;

        _psSolid?.Dispose();
        _psSolid = null;

        _psSampleWithOverlay?.Dispose();
        _psSampleWithOverlay = null;

        _psSampleWithOverlayAndPersona?.Dispose();
        _psSampleWithOverlayAndPersona = null;

        _psSampleWithPersona?.Dispose();
        _psSampleWithPersona = null;

        _psSample?.Dispose();
        _psSample = null;

        _vs?.Dispose();
        _vs = null;

        GC.SuppressFinalize(this);
    }
}
