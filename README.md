<p align="center">
  <img src="./logo.png" alt="Prism Capture" width="180" />
</p>

<h1 align="center">Prism Capture — Windows 11 Screen Recorder</h1>

<p align="center">
  A fast, Windows-native <b>screen recorder for Windows 10/11</b> built on <b>Windows.Graphics.Capture</b> (GPU capture) with a WinUI 3 UI, live preview, and recording via an external <b>FFmpeg</b> process.
</p>

<p align="center">
  Keywords: Windows 11 screen recorder • region capture • window capture • GPU capture • WinUI 3 • .NET 8 • Windows App SDK • FFmpeg
</p>

<p align="center">
  <a href="https://buymeacoffee.com/rorrimaesu">
    <img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy me a coffee" height="42" />
  </a>
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2B-0078D4" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8-512BD4" />
  <img alt="UI" src="https://img.shields.io/badge/UI-WinUI%203-2D7D9A" />
</p>

## Highlights

- **Capture modes:** Screen (primary monitor), Window, Region (crop)
- **Live preview:** GPU → D3D11 → `SwapChainPanel`
- **Crash-safe output:** writes `*.mp4.part` and renames to `*.mp4` on clean stop
- **Deep diagnostics:** persistent breadcrumbs log for end-to-end debugging

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Quick start (run the app)](#quick-start-run-the-app)
- [Where recordings are saved](#where-recordings-are-saved)
- [Logs / diagnostics](#logs--diagnostics)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [Support Prism Capture](#support-prism-capture)

## Features

- **Windows 10/11 capture** via Windows.Graphics.Capture (low overhead GPU capture)
- **Screen / window / region recording** (crop-based region)
- **Audio support** (system audio + microphone when available)
- **Hardware encoding with fallback** (NVENC → QSV → AMF → software)
- **Stable output workflow**: writes `*.mp4.part` then finalizes to `*.mp4`
- **Windows 11 look & feel**: WinUI 3 + Mica/Acrylic + Fluent motion

## Support Prism Capture

Prism Capture is free, and I’d like to keep it that way.

If it saved you time (or saved a recording from getting ruined), the simplest way to support ongoing work is a small donation:

<a href="https://buymeacoffee.com/rorrimaesu">
  <img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy me a coffee" height="42" />
</a>

Suggested contributions (pick what feels fair):

- **$3** — “This replaced a quick tool I used once”
- **$10** — “This saved me a headache / rescued a take”
- **$25** — “I use this regularly and want it to keep improving”

What donations help pay for (honestly):

- Time spent improving stability (capture edge cases, encoder fallbacks, “nothing happens” bugs)
- Better UX polish and Windows 11-native fit and finish
- More compatibility testing across GPUs/monitors/scaling setups

Prefer not to donate? A **star** and a good bug report (with `%LOCALAPPDATA%\PrismCapture\breadcrumbs.log`) helps a lot.

## Requirements

- Windows 10+ with Windows Graphics Capture support
  - Project target: `net8.0-windows10.0.19041.0`
- .NET SDK 8.x
- FFmpeg
  - The app looks for `ffmpeg.exe` next to the app executable first, otherwise tries launching `ffmpeg` from `PATH`.

## Quick start (run the app)

### Option A — Visual Studio (recommended)

1. Open `ScreenRecorder.sln`.
2. Set startup project to **ScreenRecorder.App**.
3. Choose a platform (x64 recommended; x86 is supported).
4. Press **F5**.

### Option B — Command line

From the repo root:

```powershell
# Build
dotnet build .\ScreenRecorder.sln -c Debug

# Run (launches the WinUI app)
dotnet run --project .\src\ScreenRecorder.App\ScreenRecorder.App.csproj -c Debug
```

### Option C — Run the built EXE directly

After a successful build, launch the generated exe (path varies by platform):

```powershell
# Example (x86 Debug)
.\src\ScreenRecorder.App\bin\x86\Debug\net8.0-windows10.0.19041.0\win-x86\PrismCapture.exe

# Example (x64 Debug)
.\src\ScreenRecorder.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\PrismCapture.exe
```

If FFmpeg is not found, place `ffmpeg.exe` in the same folder as `PrismCapture.exe` or install FFmpeg and ensure it’s available on `PATH`.

## Where recordings are saved

- Default output folder: your **Videos** folder (e.g. `C:\Users\<you>\Videos`).
- File naming: `PrismCapture_yyyy-MM-dd_HH-mm-ss.mp4`
- During recording the app writes: `...mp4.part`
  - On Stop, it finalizes and renames to `...mp4`.

The UI also shows the configured output folder and provides an “Open output folder” button.

## Logs / diagnostics

A persistent breadcrumbs log is written to:

- `%LOCALAPPDATA%\PrismCapture\breadcrumbs.log`

This log is intended to make “nothing happens” issues diagnosable (FFmpeg resolution, picker results, capture start/stop, encoder failures, etc.).

## How the app works (architecture)

### High-level flow

1. **User chooses capture source** (Screen / Window / Region).
2. **Preview** starts immediately using Windows.Graphics.Capture + D3D11.
3. When Record is pressed:
   - Preview is stopped (to avoid contention)
   - Recording starts:
     - Video frames are captured via `Direct3D11CaptureFramePool`
     - Frames are copied to a CPU-readable staging texture
     - Raw BGRA bytes are written to FFmpeg stdin (`pipe:0`)
     - Mixed audio floats are written to an FFmpeg named pipe (`\\.\pipe\...`)
4. When Stop is pressed:
   - FFmpeg is allowed to finalize the file
   - `.mp4.part` is renamed to `.mp4`

### Core components

- UI
  - `src/ScreenRecorder.App/Views/MainPage.xaml`
  - `src/ScreenRecorder.App/Views/MainPage.xaml.cs`
    - Wires up preview (`CapturePreview`) and recording (`AvRecorder`)
    - Enforces mode correctness (Screen/Window/Region selection must match at record time)

- ViewModel / Commands
  - `src/ScreenRecorder.App/ViewModels/MainViewModel.cs`
    - `PickCaptureSourceCommand` drives source selection
    - Holds state like `SelectedTab`, `SelectedCaptureItem`, `SelectedRegion`, output folder, and status strings

- Capture selection helpers
  - `src/ScreenRecorder.App/Helpers/MonitorInterop.cs` — resolves the primary monitor handle
  - `src/ScreenRecorder.App/Helpers/CaptureItemFactory.cs` — creates `GraphicsCaptureItem` from monitor/window handles (may fail on some configs)
  - `src/ScreenRecorder.App/Helpers/WindowInterop.cs` — initializes the system picker with the app window handle

- Preview
  - `src/ScreenRecorder.App/Services/CapturePreview.cs`
    - Uses `GraphicsCaptureSession` + D3D11 + `SwapChainPanel` for live preview

- Recording
  - `src/ScreenRecorder.App/Services/AvRecorder.cs`
    - Owns the capture session used for recording
    - Copies GPU frames into a CPU staging texture
    - Pushes raw bytes into FFmpeg
    - Tracks dropped frames and logs periodic performance stats

- Audio
  - `src/ScreenRecorder.App/Services/Audio/WasapiMixedAudioSource.cs`
    - Captures system loopback + microphone (when available)
    - Produces 48kHz stereo float samples

- FFmpeg mux/encode
  - `src/ScreenRecorder.App/Services/Ffmpeg/FfmpegAvMuxer.cs`
    - Launches FFmpeg, drains stdout/stderr (prevents deadlocks), and logs encoder initialization
    - Input ordering is **audio first, then video**, with explicit `-map` (prevents pipe deadlocks)
    - Forces `-f mp4` so `.mp4.part` works
    - Prefers hardware encode (NVENC → QSV → AMF → libx264) with fallbacks

### Capture modes (what “Screen / Window / Region” mean)

- **Screen**
  - Attempts to create a `GraphicsCaptureItem` for the **primary monitor**.
  - If that programmatic creation fails, it falls back to the system capture picker.

- **Window**
  - Uses the system capture picker to select a window.

- **Region**
  - Selects a screen first (same behavior as Screen).
  - Displays an overlay on the preview; you drag to select a rectangle.
  - The region is applied via FFmpeg `-vf crop=...`.

## Troubleshooting

### “Choose…” does nothing

The button is command-bound; when it appears to do nothing it’s usually because:

- The system picker opened behind another window
- Programmatic monitor selection failed and no fallback occurred (older builds)

Check `%LOCALAPPDATA%\PrismCapture\breadcrumbs.log` for `PickCaptureSourceAsync` entries.

### FFmpeg not found

- Put `ffmpeg.exe` next to the built `PrismCapture.exe`, or
- Install FFmpeg and ensure `ffmpeg` runs from a normal terminal `PATH`.

### Hardware encoder fails

Hardware encoders can reject unsupported sizes or parameters. The app logs FFmpeg stderr; look for errors like:

- `InitializeEncoder failed`
- `Error while opening encoder`

If this happens, try disabling “Hardware Encoding” in the UI to force software encode, or choose a different capture source/size.

## FAQ

### Is Prism Capture a Windows 11 screen recorder?

Yes — it’s designed for Windows 10/11 and uses Windows.Graphics.Capture for modern, GPU-friendly capture.

### Can it record a region (area) of the screen?

Yes. Choose **Region**, then drag to select a rectangle; the crop is applied at encode time.

### Where are recordings saved?

By default: your Windows **Videos** folder. See [Where recordings are saved](#where-recordings-are-saved).

### Do I need FFmpeg installed?

Yes. The app looks for `ffmpeg.exe` next to the executable first, and then falls back to launching `ffmpeg` from `PATH`.

### Why does it create `*.mp4.part` files?

This is intentional for crash-safety: the file is finalized and renamed to `*.mp4` on a clean stop.

## Repo layout

- `src/ScreenRecorder.App/` — main WinUI 3 application
- `prototype/` — early experiments and notes
- `PLANNING.md` — original product planning doc and long-term direction
