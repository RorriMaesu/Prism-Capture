# Prototype Flow — `Direct3D11CaptureFrame` → FFmpeg (D3D11VA)

This outlines the intended zero/low-copy flow for Phase 4.

## 1) Capture frame
- Use `Windows.Graphics.Capture` to create a `Direct3D11CaptureFramePool` and `GraphicsCaptureSession`.
- On `FrameArrived`, call `TryGetNextFrame()`.

You receive:
- `Direct3D11CaptureFrame`
  - `Surface` (`IDirect3DSurface`)
  - `SystemRelativeTime` (timestamp)
  - `ContentSize`

## 2) Extract `ID3D11Texture2D*`
- Use the WinRT interop interface `IDirect3DDxgiInterfaceAccess` to query the DXGI interface.
- Request the `ID3D11Texture2D` GUID.

The helper in [prototype/D3D11SurfaceInterop.cs](prototype/D3D11SurfaceInterop.cs) returns an `IntPtr` to the COM interface.

## 3) Initialize FFmpeg HW context (D3D11)
- Create a hardware device context:
  - `av_hwdevice_ctx_create(..., AV_HWDEVICE_TYPE_D3D11VA, ...)`
- Create a hardware frames context:
  - `AVHWFramesContext` with `format = AV_PIX_FMT_D3D11`
  - `sw_format` set to a compatible software pixel format for the encoder pipeline

## 4) Encode without CPU copy
Two common strategies:

### Strategy A (preferred): D3D11 frames into an HW encoder
- Configure the encoder to accept `AV_PIX_FMT_D3D11` frames.
- Bind `hw_frames_ctx` on the codec context.
- Wrap each `ID3D11Texture2D` as an FFmpeg `AVFrame` of format `AV_PIX_FMT_D3D11`.

### Strategy B: GPU convert + upload once
If the capture output format doesn’t match what the encoder expects:
- Convert GPU texture format using a D3D11 shader path (or copy into an intermediate texture).
- Still keep frames on GPU and hand the converted texture to FFmpeg.

## 5) Crash-safe output
- Easiest: write MKV during capture (playable up to last written cluster).
- If you must ship MP4: use fragmented MP4 flags and finalize/remux on clean stop.

## 6) Critical implementation details
- Timestamp: derive PTS from `SystemRelativeTime` or a monotonic clock; keep consistent timebase.
- Backpressure: if encoder lags, drop frames or reduce fps.
- Exclusion: ensure the main window and mini-controller are excluded from capture.

