# Prototype — D3D11 Capture Frame → FFmpeg

This folder is a focused prototype area for the hardest integration point in the plan: feeding `Windows.Graphics.Capture` GPU frames (D3D11 textures) into FFmpeg without unnecessary CPU copies.

## Goal
- Accept a captured `ID3D11Texture2D` (from `Direct3D11CaptureFrame.Surface`)
- Encode H.264 using a hardware encoder when available
- Keep the pipeline compatible with crash-safe output (fragmented MP4 or MKV)

## What’s included
- A minimal, self-contained C# code skeleton showing:
  - How to extract an `ID3D11Texture2D` from a WinRT `IDirect3DSurface`
  - How to initialize FFmpeg with a D3D11 hardware device (`AV_HWDEVICE_TYPE_D3D11VA`)
  - The structure for wrapping the texture into an FFmpeg hardware `AVFrame`

## Notes / Caveats
- This is intentionally a prototype skeleton, not a full working recorder.
- A production implementation must also handle:
  - Color format conversion (typical capture output is BGRA)
  - Encoder-specific pixel format requirements
  - Timestamping and pacing
  - Audio muxing (WASAPI) and fMP4/MKV container settings

If you want, I can turn this into a buildable WinUI 3 solution next (with the Studio window + picker + live preview), and we can iterate on the encoder bridge from there.
